using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PokeStudio.Core.SourceCode;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// Hard and up: the way on to the next place opens only once every trainer of the area is beaten (no slipping past by a
/// ledge or Rocket Hideout's spinning tiles). "On" is any exit to a place further in the playthrough: a place later in
/// the story order, or one that leads to trainers or such a place (never a Pokémon Center or a house). A guard stands on
/// each door or stair that way, and on a route's edge (a wide edge gets one guard, and stepping on the rest of it sends the
/// player back with the guard's words); talked to once the area is clear, the guards leave. Only trainers the player can
/// reach from the other ways in, without HMs, count, so the lock can't trap anyone; one that leaves after the story
/// (a FLAG_HIDE_ flag) counts as beaten once gone. The map's ON_TRANSITION hides the guards on Easy and Normal and when
/// the area is clear (a temporary flag), and turns the edge off (a temporary var).
/// </summary>
internal static partial class AreaLock
{
    public const string Mark = "Poke Studio: area lock";
    private const string GuardPrefix = "PokeStudio_AreaGuard_";
    private const string TriggerPrefix = "PokeStudio_AreaEdge_";
    private const int MaxGuards = 6;

    public sealed record Trainer(string Id, string? Flag);

    /// <summary>A map's lock: guard tiles (with their height), edge tiles, the trainers, and the temporary flag and var it uses.</summary>
    public sealed record Plan(string Map, IReadOnlyList<(int X, int Y, int Elevation)> Guards, IReadOnlyList<(int X, int Y, int Elevation)> Edges,
        IReadOnlyList<Trainer> Trainers, string Flag, string Var, string Why);

    /// <summary>Every map's links (warps and connections) and whether it has trainers: the playthrough's shape.</summary>
    public sealed class World
    {
        public Dictionary<string, string> IdByFolder { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, HashSet<string>> Links { get; } = new(StringComparer.Ordinal); // map id → map ids
        public HashSet<string> WithTrainers { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, (double Position, int Steps)> Rank { get; } = new(StringComparer.Ordinal);

        public static World Load(string root, IReadOnlyList<string> order)
        {
            var w = new World();
            var dir = Path.Combine(root, "data", "maps");
            if (!Directory.Exists(dir))
            {
                return w;
            }

            foreach (var folder in Directory.GetDirectories(dir).Order(StringComparer.Ordinal))
            {
                var file = Path.Combine(folder, "map.json");
                if (!File.Exists(file))
                {
                    continue;
                }

                var json = File.ReadAllText(file);
                var id = Regex.Match(json, "\"id\"\\s*:\\s*\"(\\w+)\"").Groups[1].Value;
                if (id.Length == 0)
                {
                    continue;
                }

                w.IdByFolder[Path.GetFileName(folder)] = id;
                var links = w.Links.TryGetValue(id, out var l) ? l : w.Links[id] = new HashSet<string>(StringComparer.Ordinal);
                foreach (Match m in Regex.Matches(json, "\"(?:dest_map|map)\"\\s*:\\s*\"(MAP_\\w+)\""))
                {
                    if (m.Groups[1].Value != id && m.Groups[1].Value != "MAP_DYNAMIC")
                    {
                        links.Add(m.Groups[1].Value);
                        (w.Links.TryGetValue(m.Groups[1].Value, out var back) ? back : w.Links[m.Groups[1].Value] = new HashSet<string>(StringComparer.Ordinal)).Add(id);
                    }
                }

                if (Regex.IsMatch(json, "\"trainer_type\"\\s*:\\s*\"TRAINER_TYPE_(NORMAL|SEE_ALL_DIRECTIONS)\""))
                {
                    w.WithTrainers.Add(id);
                }
            }

            // a place in the story order ranks by its position; others by the nearest earlier place they're reached from
            var positions = order.Select((m, i) => (m, i)).Where(x => x.m.StartsWith("MAP_", StringComparison.Ordinal))
                .GroupBy(x => x.m).ToDictionary(g => g.Key, g => (double)g.First().i, StringComparer.Ordinal);
            foreach (var (map, pos) in positions.OrderBy(p => p.Value))
            {
                w.Rank[map] = (pos, 0);
            }

            foreach (var (map, pos) in positions.OrderBy(p => p.Value))
            {
                var queue = new Queue<(string Map, int Steps)>([(map, 0)]);
                while (queue.Count > 0)
                {
                    var (at, steps) = queue.Dequeue();
                    foreach (var next in w.Links.GetValueOrDefault(at) ?? [])
                    {
                        if (!positions.ContainsKey(next) && !w.Rank.ContainsKey(next))
                        {
                            w.Rank[next] = (pos, steps + 1);
                            queue.Enqueue((next, steps + 1));
                        }
                    }
                }
            }

            return w;
        }

        private bool Later(string a, string b) =>
            Rank.TryGetValue(a, out var ra) && Rank.TryGetValue(b, out var rb) && (rb.Position > ra.Position || rb.Position == ra.Position && rb.Steps > ra.Steps);

        /// <summary>Going from <paramref name="from"/> to <paramref name="to"/> goes on: a later place with trainers, or one leading to such a place.</summary>
        public bool Onward(string from, string to)
        {
            if (!Later(from, to))
            {
                return false;
            }

            var seen = new HashSet<string>(StringComparer.Ordinal) { from, to };
            var queue = new Queue<(string Map, int Depth)>([(to, 0)]);
            while (queue.Count > 0)
            {
                var (at, depth) = queue.Dequeue();
                if (WithTrainers.Contains(at) || Rank.TryGetValue(at, out var r) && r.Steps == 0 && Later(from, at))
                {
                    return true;
                }

                if (depth < 6)
                {
                    foreach (var next in (Links.GetValueOrDefault(at) ?? []).Where(seen.Add))
                    {
                        queue.Enqueue((next, depth + 1));
                    }
                }
            }

            return false;
        }
    }

    /// <summary>The lock for a map, or null when it needs none (no trainers to beat, no way on, or no way in from behind).</summary>
    public static Plan? PlanArea(string map, World world, Func<string, string?> read, Func<string, byte[]?> readBytes, IReadOnlyDictionary<string, int> behaviors)
    {
        if (read($"data/maps/{map}/map.json") is not { } json || !world.IdByFolder.TryGetValue(map, out var id))
        {
            return null;
        }

        using var doc = JsonDocument.Parse(json);
        var rootEl = doc.RootElement;
        if (!rootEl.TryGetProperty("layout", out var layoutId) || CenterPlacement.LoadGrid(read, readBytes, layoutId.GetString()!) is not { } g)
        {
            return null;
        }

        string Str(JsonElement o, string name) => o.TryGetProperty(name, out var v) ? (v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : v.ToString()) : "";
        int Int(JsonElement o, string name, int fallback) => o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;
        var scripts = (read($"data/maps/{map}/scripts.inc") ?? "") + "\n" + (read("data/scripts/trainers.inc") ?? "");

        // the people: trainers (one per script, twins share one) and everyone else, who stands in the way
        var trainers = new List<(int X, int Y, Trainer T)>();
        var blocked = new HashSet<(int X, int Y)>();
        foreach (var o in Array(rootEl, "object_events"))
        {
            var (x, y) = (Int(o, "x", -1), Int(o, "y", -1));
            var script = Str(o, "script");
            var objFlag = Str(o, "flag");
            if (script.StartsWith(GuardPrefix, StringComparison.Ordinal))
            {
                continue;
            }

            var trainerId = TrainerOf(scripts, script);
            if (trainerId is not null && Str(o, "trainer_type") is "TRAINER_TYPE_NORMAL" or "TRAINER_TYPE_SEE_ALL_DIRECTIONS"
                && !script.StartsWith(StoryWriter.ScriptPrefix, StringComparison.Ordinal) && (objFlag is "0" or "" || objFlag.StartsWith("FLAG_HIDE_", StringComparison.Ordinal)))
            {
                trainers.Add((x, y, new Trainer(trainerId, objFlag is "0" or "" ? null : objFlag)));
            }

            // HM obstacles block (the lock counts only trainers reachable without HMs); item balls don't
            if (!Str(o, "graphics_id").Contains("ITEM_BALL", StringComparison.Ordinal))
            {
                blocked.Add((x, y));
            }
        }

        if (trainers.Count == 0)
        {
            return null;
        }

        var water = behaviors.Where(b => b.Key.Contains("WATER", StringComparison.Ordinal) && b.Key is not ("MB_SHALLOW_WATER" or "MB_PUDDLE") || b.Key == "MB_WATERFALL")
            .Select(b => b.Value).ToHashSet();
        var ledges = behaviors.Where(b => b.Key.StartsWith("MB_JUMP_", StringComparison.Ordinal)).Select(b => b.Value).ToHashSet();
        bool Floor(int x, int y) => g.Inside(x, y) && (g.At(g.Collision, x, y) == 0 || ledges.Contains(g.Behavior(g.At(g.Metatile, x, y))))
            && !water.Contains(g.Behavior(g.At(g.Metatile, x, y))) && !blocked.Contains((x, y));

        // the ways out: warps (a tile each) and edges, onward or back
        var onwardWarps = new List<(int X, int Y, int Elevation)>();
        var onwardEdges = new List<List<(int X, int Y)>>();
        var backTiles = new List<(int X, int Y)>();
        foreach (var w in Array(rootEl, "warp_events"))
        {
            var tile = (Int(w, "x", -1), Int(w, "y", -1));
            if (world.Onward(id, Str(w, "dest_map")))
            {
                onwardWarps.Add((tile.Item1, tile.Item2, Int(w, "elevation", 3)));
            }
            else
            {
                backTiles.Add(tile);
            }
        }

        foreach (var c in Array(rootEl, "connections"))
        {
            var edge = (Str(c, "direction") switch
            {
                "up" => Enumerable.Range(0, g.Width).Select(x => (x, 0)),
                "down" => Enumerable.Range(0, g.Width).Select(x => (x, g.Height - 1)),
                "left" => Enumerable.Range(0, g.Height).Select(y => (0, y)),
                "right" => Enumerable.Range(0, g.Height).Select(y => (g.Width - 1, y)),
                _ => [],
            }).Where(t => Floor(t.Item1, t.Item2)).ToList();
            if (edge.Count == 0)
            {
                continue;
            }

            if (world.Onward(id, Str(c, "map")))
            {
                onwardEdges.Add(edge);
            }
            else
            {
                backTiles.AddRange(edge);
            }
        }

        if ((onwardWarps.Count == 0 && onwardEdges.Count == 0) || backTiles.Count == 0)
        {
            return null;
        }

        // the trainers the player can reach from behind without going on
        var onward = onwardWarps.Select(w => (w.X, w.Y)).Concat(onwardEdges.SelectMany(e => e)).ToHashSet();
        var reach = new HashSet<(int X, int Y)>();
        var queue = new Queue<(int X, int Y)>();
        foreach (var t in backTiles.Where(t => g.Inside(t.X, t.Y) && reach.Add(t)))
        {
            queue.Enqueue(t);
        }

        while (queue.Count > 0)
        {
            var (x, y) = queue.Dequeue();
            foreach (var n in new[] { (x + 1, y), (x - 1, y), (x, y + 1), (x, y - 1) })
            {
                if (!onward.Contains(n) && Floor(n.Item1, n.Item2) && reach.Add(n))
                {
                    queue.Enqueue(n);
                }
            }
        }

        var counted = trainers.Where(t => new[] { (t.X + 1, t.Y), (t.X - 1, t.Y), (t.X, t.Y + 1), (t.X, t.Y - 1) }.Any(reach.Contains))
            .Select(t => t.T).DistinctBy(t => t.Id).ToList();
        if (counted.Count == 0)
        {
            return null;
        }

        // a guard on each onward door or stair, and on each onward edge (more of a wide edge than fits becomes trigger tiles)
        var guards = onwardWarps.Take(MaxGuards).ToList();
        var edges = new List<(int X, int Y, int Elevation)>();
        foreach (var edge in onwardEdges)
        {
            var tiles = edge.Where(t => reach.Contains(t) || new[] { (t.X + 1, t.Y), (t.X - 1, t.Y), (t.X, t.Y + 1), (t.X, t.Y - 1) }.Any(reach.Contains)).ToList();
            if (tiles.Count == 0)
            {
                tiles = edge;
            }

            var middle = tiles[tiles.Count / 2];
            foreach (var t in tiles)
            {
                var elevation = g.At(g.Elevation, t.X, t.Y);
                if ((tiles.Count <= 3 || t == middle) && guards.Count < MaxGuards)
                {
                    guards.Add((t.X, t.Y, elevation));
                }
                else
                {
                    edges.Add((t.X, t.Y, elevation));
                }
            }
        }

        // two edges can share a corner tile
        guards = [.. guards.DistinctBy(t => (t.X, t.Y))];
        edges = [.. edges.DistinctBy(t => (t.X, t.Y)).Where(t => guards.All(gd => (gd.X, gd.Y) != (t.X, t.Y)))];

        // a temporary flag and var the map doesn't use already
        var used = scripts + json;
        var flag = Enumerable.Range(0, 0x20).Reverse().Select(n => $"FLAG_TEMP_{n:X}").FirstOrDefault(f => !Regex.IsMatch(used, $@"\b{f}\b"));
        var var = "DCBA9876".Select(c => $"VAR_TEMP_{c}").FirstOrDefault(v => !Regex.IsMatch(used, $@"\b{v}\b"));
        if (flag is null || var is null)
        {
            return null;
        }

        return new Plan(map, guards, edges, counted, flag, var,
            $"{counted.Count} trainers, {onwardWarps.Count} onward doors, {onwardEdges.Count} onward edges, {guards.Count} guards, {edges.Count} edge tiles");
    }

    /// <summary>The map.json with the guards and edge tiles (or without, when <paramref name="plan"/> is null).</summary>
    public static string MapJson(string json, Plan? plan, string graphics)
    {
        json = Clean(json);
        if (plan is null)
        {
            return json;
        }

        var nl = CScanner.LineEnding(json);
        var objects = plan.Guards.Select((t, i) => new[]
        {
            "{",
            "  \"type\": \"object\",",
            $"  \"graphics_id\": \"{graphics}\",",
            $"  \"x\": {N(t.X)},",
            $"  \"y\": {N(t.Y)},",
            $"  \"elevation\": {N(t.Elevation)},",
            "  \"movement_type\": \"MOVEMENT_TYPE_FACE_DOWN\",",
            "  \"movement_range_x\": 0,",
            "  \"movement_range_y\": 0,",
            "  \"trainer_type\": \"TRAINER_TYPE_NONE\",",
            "  \"trainer_sight_or_berry_tree_id\": \"0\",",
            $"  \"script\": \"{GuardPrefix}{plan.Map}\",",
            $"  \"flag\": \"{plan.Flag}\"",
            "}",
        }).ToList();
        var triggers = plan.Edges.Select(t => new[]
        {
            "{",
            "  \"type\": \"trigger\",",
            $"  \"x\": {N(t.X)},",
            $"  \"y\": {N(t.Y)},",
            $"  \"elevation\": {N(t.Elevation)},",
            $"  \"var\": \"{plan.Var}\",",
            "  \"var_value\": \"0\",",
            $"  \"script\": \"{TriggerPrefix}{plan.Map}\"",
            "}",
        }).ToList();
        return Append(Append(json, "object_events", objects, nl), "coord_events", triggers, nl);
    }

    /// <summary>The first local id of the guards (they're added after the map's own people).</summary>
    public static int FirstGuardId(string cleanJson) => CountObjects(cleanJson) + 1;

    /// <summary>
    /// The map's scripts.inc with the lock: an ON_TRANSITION entry first (hiding the guards and turning the edge off on Easy
    /// and Normal or once the area is clear, then the map's own ON_TRANSITION), the guards' and the edge's scripts.
    /// </summary>
    public static string Script(string text, string map, Plan? plan, int firstGuardId)
    {
        var nl = CScanner.LineEnding(text);
        var clean = CleanScript(text);
        if (plan is null)
        {
            return clean;
        }

        var table = Regex.Match(clean, $@"^{Regex.Escape(map)}_MapScripts::[^\n]*\n", RegexOptions.Multiline);
        if (!table.Success)
        {
            return clean;
        }

        var tableEnd = clean.IndexOf("\t.byte 0", table.Index, StringComparison.Ordinal);
        var own = Regex.Match(clean[table.Index..(tableEnd < 0 ? clean.Length : tableEnd)], @"map_script\s+MAP_SCRIPT_ON_TRANSITION\s*,\s*(\w+)");
        var label = $"PokeStudio_Area_{map}";
        var b = new StringBuilder();
        void L(string line) => b.Append(line).Append(" @ ").Append(Mark).Append(nl);

        // "is everyone beaten?": jumps to the label when someone isn't
        void Unbeaten(string whenSo, string tag)
        {
            for (var i = 0; i < plan.Trainers.Count; i++)
            {
                var t = plan.Trainers[i];
                if (t.Flag is not null)
                {
                    L($"\tgoto_if_set {t.Flag}, {label}_{tag}{i}");
                }

                L($"\tgoto_if_undefeated {t.Id}, {whenSo}");
                if (t.Flag is not null)
                {
                    L($"{label}_{tag}{i}:");
                }
            }
        }

        b.Append(nl);
        L($"{label}::");
        L($"\tcallnative {Chokepoints.Native}");
        L($"\tgoto_if_eq VAR_RESULT, FALSE, {label}_Open");
        Unbeaten($"{label}_Done", "T");
        L($"{label}_Open:");
        L($"\tsetflag {plan.Flag}");
        L($"\tsetvar {plan.Var}, 1");
        L($"{label}_Done:");
        L(own.Success ? $"\tgoto {own.Groups[1].Value}" : "\tend");

        // the guard: waits while someone is left, then leaves (with every other guard of the map)
        L($"{GuardPrefix}{map}::");
        L("\tlock");
        L("\tfaceplayer");
        Unbeaten($"{label}_Wait", "G");
        L($"\tmsgbox PokeStudio_Text_AreaClear, MSGBOX_DEFAULT");
        L("\tclosemessage");
        L("\tfadescreen FADE_TO_BLACK");
        L($"\tsetflag {plan.Flag}");
        L($"\tsetvar {plan.Var}, 1");
        for (var i = 0; i < plan.Guards.Count; i++)
        {
            L($"\tremoveobject {N(firstGuardId + i)}");
        }

        L("\tfadescreen FADE_FROM_BLACK");
        L("\trelease");
        L("\tend");
        L($"{label}_Wait:");
        L($"\tmsgbox PokeStudio_Text_AreaWait, MSGBOX_DEFAULT");
        L("\trelease");
        L("\tend");

        // the rest of a wide edge: the guard's words, and a step back the way the player came
        if (plan.Edges.Count > 0)
        {
            L($"{TriggerPrefix}{map}::");
            L("\tlockall");
            Unbeaten($"{label}_Back", "E");
            L($"\tsetvar {plan.Var}, 1");
            L("\treleaseall");
            L("\tend");
            L($"{label}_Back:");
            L($"\tmsgbox PokeStudio_Text_AreaWait, MSGBOX_DEFAULT");
            L("\tclosemessage");
            L("\tspecialvar VAR_RESULT, GetPlayerFacingDirection");
            L("\tgoto_if_eq VAR_RESULT, DIR_SOUTH, PokeStudio_EventScript_AreaStepUp");
            L("\tgoto_if_eq VAR_RESULT, DIR_NORTH, PokeStudio_EventScript_AreaStepDown");
            L("\tgoto_if_eq VAR_RESULT, DIR_WEST, PokeStudio_EventScript_AreaStepRight");
            L("\tgoto PokeStudio_EventScript_AreaStepLeft");
        }

        var withEntry = clean.Insert(table.Index + table.Length, $"\tmap_script MAP_SCRIPT_ON_TRANSITION, {label} @ {Mark}{nl}");
        return withEntry.TrimEnd('\r', '\n') + nl + b;
    }

    /// <summary>What every lock shares: the words and the steps back (in the options script).</summary>
    public static string Shared(string nl)
    {
        var b = new StringBuilder();
        foreach (var (dir, move) in new[] { ("Up", "walk_up"), ("Down", "walk_down"), ("Left", "walk_left"), ("Right", "walk_right") })
        {
            b.Append($"PokeStudio_EventScript_AreaStep{dir}::{nl}\tapplymovement OBJ_EVENT_ID_PLAYER, PokeStudio_Movement_AreaStep{dir}{nl}\twaitmovement 0{nl}\treleaseall{nl}\tend{nl}{nl}");
            b.Append($"PokeStudio_Movement_AreaStep{dir}:{nl}\t{move}{nl}\tstep_end{nl}{nl}");
        }

        b.Append($"PokeStudio_Text_AreaWait:{nl}\t.string \"Hold on! You can't go on until\\n\"{nl}\t.string \"you've beaten every trainer here.$\"{nl}{nl}");
        b.Append($"PokeStudio_Text_AreaClear:{nl}\t.string \"You've beaten every trainer here.\\n\"{nl}\t.string \"Go on ahead!$\"{nl}{nl}");
        return b.ToString();
    }

    public static string CleanScript(string text)
    {
        var clean = Regex.Replace(text, $@"^[^\n]*@ {Regex.Escape(Mark)}[^\n]*\r?\n", "", RegexOptions.Multiline);
        return clean == text ? text : clean.TrimEnd('\r', '\n') + CScanner.LineEnding(text);
    }

    private static string Clean(string json)
    {
        foreach (var prefix in new[] { GuardPrefix, TriggerPrefix })
        {
            while (Regex.Match(json, $"\"script\": \"({prefix}\\w+)\"") is { Success: true } m)
            {
                json = CenterPlacement.RemoveObject(json, m.Groups[1].Value);
            }
        }

        return json;
    }

    private static string? TrainerOf(string scripts, string script)
    {
        var at = script.Length == 0 ? -1 : scripts.IndexOf(script + "::", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var end = scripts.IndexOf("\n\tend", at, StringComparison.Ordinal) is var e and >= 0 ? e : scripts.Length;
        var battle = Regex.Match(scripts[at..end], @"\btrainerbattle\w*\s+(?:TRAINER_BATTLE_\w+\s*,\s*)?(TRAINER_\w+)");
        return battle.Success ? battle.Groups[1].Value : null;
    }

    private static IEnumerable<JsonElement> Array(JsonElement root, string name) =>
        root.TryGetProperty(name, out var a) && a.ValueKind == JsonValueKind.Array ? a.EnumerateArray().ToList() : [];

    private static int CountObjects(string json)
    {
        using var doc = JsonDocument.Parse(json);
        return Array(doc.RootElement, "object_events").Count();
    }

    private static string Append(string json, string name, List<string[]> items, string nl)
    {
        if (items.Count == 0)
        {
            return json;
        }

        var key = json.IndexOf($"\"{name}\"", StringComparison.Ordinal);
        var open = key < 0 ? -1 : json.IndexOf('[', key);
        if (open < 0)
        {
            return json;
        }

        var close = CScanner.FindMatching(json, open);
        var hasItems = json[(open + 1)..close].Contains('{', StringComparison.Ordinal);
        var lineStart = json.LastIndexOf('\n', open) + 1;
        var indent = new string(' ', open - lineStart - json[lineStart..open].TrimStart().Length) + "  ";
        var text = string.Join("," + nl, items.Select(lines => string.Join(nl, lines.Select(l => indent + l))));
        if (hasItems)
        {
            var last = json.LastIndexOf('}', close);
            return json.Insert(last + 1, "," + nl + text);
        }

        return json.Remove(open + 1, close - open - 1).Insert(open + 1, nl + text + nl + indent[..^2]);
    }

    private static string N(int n) => n.ToString(CultureInfo.InvariantCulture);
}
