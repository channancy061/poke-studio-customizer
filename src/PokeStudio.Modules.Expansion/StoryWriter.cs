using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.SourceCode;
using PokeStudio.Core.Story;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// Story battles in an expansion project: the trainer (trainers.party, written with the other trainers, plus its name in
/// the trainer list), a trainer on the map who looks at the tile in front of an entrance, the battle script, and an
/// optional level-cap step that uses the trainer's own "beaten" flag.
/// </summary>
internal static partial class StoryWriter
{
    public const string Script = "data/scripts/poke_studio_story.inc";
    public const string Opponents = "include/constants/opponents.h";
    public const string ScriptPrefix = "PokeStudio_Story_";
    private const string Mark = "Poke Studio story battle";
    private const int MaxSight = 4;

    /// <summary>Maps with entrances: (folder, place name), outdoor maps first.</summary>
    public static IReadOnlyList<(string Map, string Name)> Maps(string root)
    {
        var dir = Path.Combine(root, "data", "maps");
        if (!Directory.Exists(dir))
        {
            return [];
        }

        var result = new List<(string Map, string Name, bool Outdoor)>();
        foreach (var folder in Directory.GetDirectories(dir).Order(StringComparer.Ordinal))
        {
            var file = Path.Combine(folder, "map.json");
            if (!File.Exists(file))
            {
                continue;
            }

            var json = File.ReadAllText(file);
            if (!json.Contains("\"dest_map\"", StringComparison.Ordinal))
            {
                continue;
            }

            var name = Path.GetFileName(folder);
            var outdoor = Regex.IsMatch(json, "\"map_type\"\\s*:\\s*\"MAP_TYPE_(TOWN|CITY|ROUTE)\"");
            result.Add((name, Place(name), outdoor));
        }

        return result.OrderBy(m => m.Outdoor ? 0 : 1).ThenBy(m => m.Name, StringComparer.Ordinal).Select(m => (m.Map, m.Name)).ToList();
    }

    /// <summary>"PewterCity_Gym" → "Pewter City Gym".</summary>
    public static string Place(string folder) => ExpansionModule.MapPlace(folder);

    private sealed record Thing(int X, int Y);

    /// <summary>
    /// Where a story battle can go on a map: for each entrance, the tile in front of it and a free spot 2–4 tiles to the
    /// side (or below) that sees it. Spots that would cut off part of the map are left out.
    /// </summary>
    public static IReadOnlyList<StorySpot> Spots(string root, string map)
    {
        string? Read(string rel) => File.Exists(Path.Combine(root, rel)) ? File.ReadAllText(Path.Combine(root, rel)) : null;
        byte[]? ReadBytes(string rel) => File.Exists(Path.Combine(root, rel)) ? File.ReadAllBytes(Path.Combine(root, rel)) : null;
        if (Read($"data/maps/{map}/map.json") is not { } json)
        {
            return [];
        }

        using var doc = JsonDocument.Parse(json);
        var rootEl = doc.RootElement;
        if (!rootEl.TryGetProperty("layout", out var layoutId) || CenterPlacement.LoadGrid(Read, ReadBytes, layoutId.GetString()!) is not { } g)
        {
            return [];
        }

        List<Thing> Things(string name) => rootEl.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(o => o.TryGetProperty("x", out _) && o.TryGetProperty("y", out _))
                .Select(o => new Thing(o.GetProperty("x").GetInt32(), o.GetProperty("y").GetInt32())).ToList()
            : [];

        var objects = Things("object_events");

        // people that are always there; others (like a guard who leaves once a story event is done) have a flag
        var alwaysThere = rootEl.TryGetProperty("object_events", out var objs) && objs.ValueKind == JsonValueKind.Array
            ? objs.EnumerateArray().Where(o => o.TryGetProperty("x", out _) && o.TryGetProperty("y", out _)
                    && (!o.TryGetProperty("flag", out var f) || f.ValueKind != JsonValueKind.String || f.GetString() is "0" or ""))
                .Select(o => new Thing(o.GetProperty("x").GetInt32(), o.GetProperty("y").GetInt32())).ToList()
            : [];
        var signs = Things("bg_events");
        var triggers = Things("coord_events");
        var warps = rootEl.TryGetProperty("warp_events", out var w) ? w.EnumerateArray().ToList() : [];
        var warpTiles = warps.Select(x => new Thing(x.GetProperty("x").GetInt32(), x.GetProperty("y").GetInt32())).ToList();
        bool Occupied(int x, int y) => objects.Any(o => o.X == x && o.Y == y) || warpTiles.Any(o => o.X == x && o.Y == y)
            || signs.Any(o => o.X == x && o.Y == y) || triggers.Any(o => o.X == x && o.Y == y);
        bool Walkable(int x, int y) => g.Inside(x, y) && g.At(g.Collision, x, y) == 0;
        (int X, int Y)? FrontOf(int x, int y)
        {
            // building doors are entered by walking up; caves and stairs from whichever side is open
            foreach (var (dx, dy) in new[] { (0, 1), (-1, 0), (1, 0), (0, -1) })
            {
                if (Walkable(x + dx, y + dy) && !warpTiles.Any(t => t.X == x + dx && t.Y == y + dy))
                {
                    return (x + dx, y + dy);
                }
            }

            return null;
        }

        var fronts = warpTiles.Select(t => FrontOf(t.X, t.Y)).Where(f => f is not null).Select(f => f!.Value).ToHashSet();
        // entrances: warps to the same place whose front tiles sit side by side count as one (a door two tiles wide)
        var entrances = new List<(int DoorX, int DoorY, string Dest, List<(int X, int Y)> Fronts)>();
        foreach (var warp in warps)
        {
            int wx = warp.GetProperty("x").GetInt32(), wy = warp.GetProperty("y").GetInt32();
            var dest = warp.TryGetProperty("dest_map", out var d) ? d.GetString() ?? "" : "";
            if (FrontOf(wx, wy) is not { } front || dest.Length == 0)
            {
                continue;
            }

            var joined = entrances.FirstOrDefault(e => e.Dest == dest && e.Fronts.Any(f => f.Y == front.Y && Math.Abs(f.X - front.X) <= 1));
            if (joined.Fronts is not null)
            {
                if (!joined.Fronts.Contains(front))
                {
                    joined.Fronts.Add(front);
                }
            }
            else
            {
                entrances.Add((wx, wy, dest, [front]));
            }
        }

        var result = new List<StorySpot>();
        foreach (var (wx, wy, dest, row) in entrances)
        {
            int minX = row.Min(f => f.X), maxX = row.Max(f => f.X), fy = row[0].Y;
            var front = (X: minX, Y: fy);
            var width = maxX - minX;

            // a map can be split into areas (Route 23: the way into Victory Road and the way out): check this entrance's own
            var reachable = Reachable(g, front.X, front.Y, -1, -1);

            // to the left or right of the front tiles (looking along the row, so a wide door is covered too), or below a
            // narrow one (looking up the path)
            var options = new List<(int X, int Y, Facing Facing, int Sight)>();
            for (var gap = 2; gap <= MaxSight; gap++)
            {
                options.Add((minX - gap, fy, Facing.Right, gap + width));
                options.Add((maxX + gap, fy, Facing.Left, gap + width));
            }

            for (var gap = 2; gap <= MaxSight && width == 0; gap++)
            {
                options.Add((front.X, front.Y + gap, Facing.Up, gap));
            }

            foreach (var (sx, sy, facing, sight) in options)
            {
                if (!Walkable(sx, sy) || Occupied(sx, sy) || fronts.Contains((sx, sy)))
                {
                    continue;
                }

                // nothing between the trainer and the far end of the front tiles, so they see all of them
                var (stepX, stepY) = facing switch { Facing.Right => (1, 0), Facing.Left => (-1, 0), _ => (0, -1) };
                var clear = true;
                for (var k = 1; k <= sight && clear; k++)
                {
                    // only walls and people block a trainer's view (step triggers and signs don't)
                    clear = Walkable(sx + (stepX * k), sy + (stepY * k)) && !alwaysThere.Any(o => o.X == sx + (stepX * k) && o.Y == sy + (stepY * k));
                }

                if (!clear)
                {
                    continue;
                }

                // standing there must not cut off any part of the map
                var after = Reachable(g, front.X, front.Y, sx, sy);
                if (reachable.Any(t => !(t.X == sx && t.Y == sy) && !after.Contains(t)))
                {
                    continue;
                }

                result.Add(new StorySpot(map, StoryBattles.PlaceName(dest), wx, wy, front.X, front.Y, sx, sy, g.At(g.Elevation, sx, sy), facing, sight));
                break;
            }
        }

        return result;
    }

    private static HashSet<(int X, int Y)> Reachable(CenterPlacement.Grid g, int sx, int sy, int blockX, int blockY)
    {
        var seen = new HashSet<(int, int)> { (sx, sy) };
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((sx, sy));
        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            foreach (var (dx, dy) in new[] { (0, 1), (0, -1), (1, 0), (-1, 0) })
            {
                var (ax, ay) = (x + dx, y + dy);
                if (g.Inside(ax, ay) && g.At(g.Collision, ax, ay) == 0 && !(ax == blockX && ay == blockY) && seen.Add((ax, ay)))
                {
                    queue.Enqueue((ax, ay));
                }
            }
        }

        return seen;
    }

    /// <summary>The overworld sprites the project has (OBJ_EVENT_GFX_…), people first.</summary>
    public static IReadOnlyList<string> Looks(string root)
    {
        var file = Path.Combine(root, "include", "constants", "event_objects.h");
        if (!File.Exists(file))
        {
            return [];
        }

        // "#define OBJ_EVENT_GFX_LASS 12" (Emerald) or an enum member "OBJ_EVENT_GFX_LASS," (FireRed)
        return Regex.Matches(File.ReadAllText(file), @"^(?:#define\s+|\s+)(OBJ_EVENT_GFX_\w+)\s*(?:\d+|,|=)", RegexOptions.Multiline)
            .Select(m => m.Groups[1].Value)
            .Where(k => !k.StartsWith("OBJ_EVENT_GFX_VAR_", StringComparison.Ordinal) && !k.Contains("SPECIES", StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal).ToList();
    }

    private const string MapsLine = "@ Maps: ";

    /// <summary>
    /// Map files a save may write: the maps with story battles now, and the maps the last save put them on (listed in
    /// the generated script, so finding them doesn't mean reading every map).
    /// </summary>
    public static IEnumerable<string> MapFiles(string root, GameData data)
    {
        var maps = new HashSet<string>(StringComparer.Ordinal);
        foreach (var b in data.Story?.Battles ?? [])
        {
            maps.Add(b.Map);
        }

        maps.UnionWith(SavedMaps(root));
        return maps.Select(m => $"data/maps/{m}/map.json");
    }

    private static IEnumerable<string> SavedMaps(string root)
    {
        var file = Path.Combine(root, Script);
        if (!File.Exists(file))
        {
            return [];
        }

        var line = File.ReadLines(file).Take(3).FirstOrDefault(l => l.StartsWith(MapsLine, StringComparison.Ordinal));
        return line is null ? [] : line[MapsLine.Length..].Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>New contents for every file that needs to change (files already up to date are left out).</summary>
    public static Dictionary<string, string> Plan(string root, GameData data, Func<string, string?> read)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        void Put(string file, string? before, string after)
        {
            if (before is not null && before != after)
            {
                result[file] = after;
            }
        }

        var battles = (data.Story?.Battles ?? []).Where(b => data.Trainers.Any(t => t.Key == b.TrainerKey)).ToList();
        var storyTrainers = data.Trainers.Where(t => t.Key.StartsWith(StoryBattles.TrainerPrefix, StringComparison.Ordinal) && !t.Key.Contains('@', StringComparison.Ordinal))
            .Select(t => t.Key).Distinct(StringComparer.Ordinal).ToList();

        // 1. the trainer list: our names just before TRAINERS_COUNT
        if (read(Opponents) is { } opponents)
        {
            Put(Opponents, opponents, Opponent(opponents, storyTrainers));
        }

        // 2. the scripts (an empty file when there are none, so the include stays valid)
        var nl = "\n";
        var script = new StringBuilder();
        script.Append("@ Generated by Poke Studio: story battles. Edit them in the app (Story battles).").Append(nl);
        script.Append(MapsLine).Append(string.Join(", ", battles.Select(b => b.Map).Distinct(StringComparer.Ordinal))).Append(nl);
        foreach (var b in battles)
        {
            var label = ScriptPrefix + b.Id;
            script.Append(nl).Append(label).Append("::").Append(nl)
                .Append(CultureInfo.InvariantCulture, $"\ttrainerbattle_single {b.TrainerKey}, {label}_Intro, {label}_Defeat").Append(nl)
                .Append(CultureInfo.InvariantCulture, $"\tmsgbox {label}_After, MSGBOX_AUTOCLOSE").Append(nl)
                .Append("\tend").Append(nl);
            foreach (var (suffix, text) in new[] { ("Intro", b.Intro), ("Defeat", b.Defeat), ("After", b.After) })
            {
                script.Append(nl).Append(label).Append('_').Append(suffix).Append(':').Append(nl)
                    .Append(TextScripts.Format(text.Length == 0 ? "…" : text, "\t", true, nl)).Append(nl);
            }
        }

        Put(Script, read(Script) ?? "", script.ToString());
        if (read(GameOptionsWriter.EventScripts) is { } events && !events.Contains("poke_studio_story.inc", StringComparison.Ordinal))
        {
            var enl = CScanner.LineEnding(events);
            Put(GameOptionsWriter.EventScripts, events, events.TrimEnd('\r', '\n') + enl + enl + $"\t.include \"{Script}\" @ {Mark}" + enl);
        }

        // 3. level caps: a "beaten" flag per major battle and its step in the list
        if (read(CapsFiles.Source) is { } caps)
        {
            Put(CapsFiles.Source, caps, Caps(caps, battles, data));
        }

        // 4. the trainers on the maps
        foreach (var file in MapFiles(root, data))
        {
            if (read(file) is { } json)
            {
                var map = file.Split('/')[2];
                Put(file, json, MapObjects(json, battles.Where(b => b.Map == map).ToList()));
            }
        }

        return result;
    }

    /// <summary>
    /// The trainer list with our trainers: FireRed's enum gets names before TRAINERS_COUNT; Emerald numbers its trainers
    /// with #defines, so ours get the numbers after the last one and the count (and, when the room runs out, the maximum)
    /// is raised by marked lines that leave the original lines as they were.
    /// </summary>
    internal static string Opponent(string text, IReadOnlyList<string> keys)
    {
        var nl = CScanner.LineEnding(text);
        var lines = Regex.Replace(text, $@"^[^\n]*// {Mark}\r?\n", "", RegexOptions.Multiline);
        if (keys.Count == 0)
        {
            return lines;
        }

        var count = Regex.Match(lines, @"^([ \t]*)TRAINERS_COUNT,", RegexOptions.Multiline);
        if (count.Success)
        {
            var add = string.Concat(keys.Select(k => $"{count.Groups[1].Value}{k}, // {Mark}{nl}"));
            return lines.Insert(count.Index, add);
        }

        if (DefinedCounts(lines) is not { } d)
        {
            return lines;
        }

        var block = new System.Text.StringBuilder();
        for (var i = 0; i < keys.Count; i++)
        {
            block.Append(System.Globalization.CultureInfo.InvariantCulture, $"#define {keys[i]} ({d.Count + i}) // {Mark}{nl}");
        }

        block.Append(System.Globalization.CultureInfo.InvariantCulture, $"#undef {d.CountName} // {Mark}{nl}#define {d.CountName} ({d.Count + keys.Count}) // {Mark}{nl}");
        lines = lines.Insert(d.CountEnd, block.ToString());
        if (d.Count + keys.Count > d.Max && DefinedCounts(lines) is { } after)
        {
            // more room for "beaten" flags (old saves of the hack won't match any more: the checks say so)
            var max = ((d.Count + keys.Count + 7) / 8) * 8;
            lines = lines.Insert(after.MaxEnd, $"#undef {d.MaxName} // {Mark}{nl}#define {d.MaxName} ({max}) // {Mark}{nl}");
        }

        return lines;
    }

    /// <summary>Emerald's "#define TRAINERS_COUNT_EMERALD 855" and "#define MAX_TRAINERS_COUNT_EMERALD 864" (null for other layouts).</summary>
    internal static (string CountName, int Count, int CountEnd, string MaxName, int Max, int MaxEnd)? DefinedCounts(string text)
    {
        var count = Regex.Match(text, @"^#define[ \t]+(TRAINERS_COUNT\w*)[ \t]+(\d+)[^\n]*\n", RegexOptions.Multiline);
        var max = Regex.Match(text, @"^#define[ \t]+(MAX_TRAINERS_COUNT\w*)[ \t]+(\d+)[^\n]*\n", RegexOptions.Multiline);
        return count.Success && max.Success
            ? (count.Groups[1].Value, int.Parse(count.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), count.Index + count.Length,
                max.Groups[1].Value, int.Parse(max.Groups[2].Value, System.Globalization.CultureInfo.InvariantCulture), max.Index + max.Length)
            : null;
    }

    internal static string Caps(string text, IReadOnlyList<StoryBattle> battles, GameData data)
    {
        var nl = CScanner.LineEnding(text);

        // take out what an earlier save added, then add what's wanted now
        text = Regex.Replace(text, $@"^[^\n]*// {Mark}\r?\n", "", RegexOptions.Multiline);
        var major = battles.Where(b => b.Before is not null).ToList();
        if (major.Count == 0)
        {
            return text;
        }

        var lastInclude = Regex.Matches(text, @"^#include[^\n]*\n", RegexOptions.Multiline).LastOrDefault();
        var defines = new StringBuilder();
        defines.Append($"#include \"constants/opponents.h\" // {Mark}{nl}");
        foreach (var b in major)
        {
            defines.Append(CultureInfo.InvariantCulture, $"#define {b.BeatFlag} (TRAINER_FLAGS_START + {b.TrainerKey}) // {Mark}{nl}");
        }

        text = text.Insert(lastInclude is null ? 0 : lastInclude.Index + lastInclude.Length, defines.ToString());
        foreach (var b in major)
        {
            var at = Regex.Match(text, $@"^([ \t]*)\{{\s*{b.Before}\s*,", RegexOptions.Multiline);
            if (!at.Success)
            {
                continue;
            }

            var level = data.Rules?.LevelCaps.FirstOrDefault(c => c.Milestone == b.BeatFlag)?.Level
                ?? data.Trainers.FirstOrDefault(t => t.Key == b.TrainerKey)?.Party.Select(p => p.Level).DefaultIfEmpty(1).Max() ?? 1;
            text = text.Insert(at.Index, $"{at.Groups[1].Value}{{{b.BeatFlag}, {level.ToString(CultureInfo.InvariantCulture)}}}, // {Mark}{nl}");
        }

        return text;
    }

    internal static string MapObjects(string json, IReadOnlyList<StoryBattle> battles)
    {
        var nl = CScanner.LineEnding(json);

        // remove our trainers from an earlier save (their scripts start with PokeStudio_Story_)
        while (json.IndexOf($"\"{ScriptPrefix}", StringComparison.Ordinal) is var at and >= 0)
        {
            var open = json.LastIndexOf('{', at);
            var close = json.IndexOf('}', at);
            var before = json.LastIndexOf(',', open);
            var prevClose = json.LastIndexOf('}', open);
            var prevOpen = json.LastIndexOf('[', open);
            if (before > Math.Max(prevClose, prevOpen))
            {
                json = json.Remove(before, close + 1 - before); // ", { … }" after another object
            }
            else
            {
                var after = json.IndexOf(',', close);
                var nextOpen = json.IndexOf('{', close);
                var arrayEnd = json.IndexOf(']', close);
                json = after >= 0 && after < arrayEnd && (nextOpen < 0 || after < nextOpen)
                    ? json.Remove(open, after + 1 - open).TrimStartAt(open) // first of several: drop "{ … },"
                    : json.Remove(open, close + 1 - open);                   // the only one
            }
        }

        if (battles.Count == 0)
        {
            return json;
        }

        var key = json.IndexOf("\"object_events\"", StringComparison.Ordinal);
        var openArray = key < 0 ? -1 : json.IndexOf('[', key);
        if (openArray < 0)
        {
            return json;
        }

        var typed = json.Contains("\"type\": \"object\"", StringComparison.Ordinal);
        var blocks = battles.Select(b => string.Join(nl, new[]
        {
            "{",
            typed ? "  \"type\": \"object\"," : null,
            $"  \"graphics_id\": \"{b.Look}\",",
            $"  \"x\": {b.X.ToString(CultureInfo.InvariantCulture)},",
            $"  \"y\": {b.Y.ToString(CultureInfo.InvariantCulture)},",
            $"  \"elevation\": {b.Elevation.ToString(CultureInfo.InvariantCulture)},",
            $"  \"movement_type\": \"MOVEMENT_TYPE_FACE_{b.Facing.ToString().ToUpperInvariant()}\",",
            "  \"movement_range_x\": 0,",
            "  \"movement_range_y\": 0,",
            "  \"trainer_type\": \"TRAINER_TYPE_NORMAL\",",
            $"  \"trainer_sight_or_berry_tree_id\": \"{b.Sight.ToString(CultureInfo.InvariantCulture)}\",",
            $"  \"script\": \"{ScriptPrefix}{b.Id}\",",
            "  \"flag\": \"0\"",
            "}",
        }.Where(l => l is not null).Select(l => "    " + l)));
        var text = string.Join("," + nl, blocks);
        var closeArray = CScanner.FindMatching(json, openArray);
        var lastObject = json.LastIndexOf('}', closeArray);
        return lastObject > openArray
            ? json.Insert(lastObject + 1, "," + nl + text)
            : json.Remove(openArray + 1, closeArray - openArray - 1).Insert(openArray + 1, nl + text + nl + "  ");
    }

    private static string TrimStartAt(this string s, int at)
    {
        var end = at;
        while (end < s.Length && char.IsWhiteSpace(s[end]))
        {
            end++;
        }

        return s.Remove(at, end - at);
    }
}
