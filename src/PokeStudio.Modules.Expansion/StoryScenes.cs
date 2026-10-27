using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PokeStudio.Core.SourceCode;
using PokeStudio.Core.Story;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// Story battles with a scene: the trainer waits inside the gym or cave and comes out when the player steps in front of
/// the entrance (the door opens, they walk out, see the player and battle), then walks away after losing. A loss means
/// the scene plays again next time. The person is hidden by a temporary flag that the map's ON_TRANSITION script sets
/// every time the map loads; the step trigger uses a temporary variable that the same script sets once they're beaten.
/// </summary>
internal static partial class StoryWriter
{
    private const string SceneMark = "Poke Studio story scene";
    private static readonly string[] Doors = ["MB_WARP_DOOR", "MB_ANIMATED_DOOR", "MB_PETALBURG_GYM_DOOR"];

    /// <summary>A planned scene: the person's local id, their temporary flag and variable, and the moves.</summary>
    internal sealed record Scene(StoryBattle Battle, int LocalId, string TempFlag, string TempVar, string ComeOut, bool Door,
        string? PlayerStep, IReadOnlyList<string> WalkAway)
    {
        /// <summary>
        /// The step of the story before this battle (e.g. FLAG_BADGE04_GET): until it's done the player is turned back at
        /// the entrance, so the battle (and its level) can't come too early. Null: from the start.
        /// </summary>
        public string? Required { get; init; }
    }

    /// <summary>The level-cap step just before a battle's own (not another story battle's), or null for the first.</summary>
    internal static string? RequiredStep(Core.Modules.GameData data, StoryBattle b)
    {
        var caps = data.Rules?.LevelCaps ?? [];
        if (!b.TurnsBackEarly)
        {
            return null;
        }

        var at = caps.ToList().FindIndex(c => c.Milestone == b.Before);
        for (var i = at - 1; i >= 0; i--)
        {
            if (!caps[i].Milestone.StartsWith("FLAG_PS_BEAT_", StringComparison.Ordinal))
            {
                return caps[i].Milestone;
            }
        }

        return null;
    }

    private static string Ordinal(int n) => n switch { 1 => "1st", 2 => "2nd", 3 => "3rd", _ => $"{n}th" };

    private static string Way(int dx, int dy) => (dx, dy) switch { (0, 1) => "down", (0, -1) => "up", (1, 0) => "right", _ => "left" };

    private static Facing FacingOf(int dx, int dy) => (dx, dy) switch { (0, 1) => Facing.Down, (0, -1) => Facing.Up, (1, 0) => Facing.Right, _ => Facing.Left };

    /// <summary>Temporary flags and variables the shared scripts and the game code use (they may run on any map).</summary>
    private static HashSet<string> CommonTemps(string root)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        foreach (var dir in new[] { Path.Combine(root, "data", "scripts"), Path.Combine(root, "src") }.Where(Directory.Exists))
        {
            foreach (var file in Directory.EnumerateFiles(dir, "*.*", SearchOption.AllDirectories).Where(f => f.EndsWith(".inc", StringComparison.Ordinal) || f.EndsWith(".c", StringComparison.Ordinal)))
            {
                foreach (Match m in TempName().Matches(File.ReadAllText(file)))
                {
                    used.Add(m.Value);
                }
            }
        }

        return used;
    }

    /// <summary>The scenes of one map (battles in the order they go on the map; the first gets local id <paramref name="firstId"/>).</summary>
    private static List<Scene> PlanScenes(string root, string map, IReadOnlyList<(StoryBattle Battle, int LocalId)> battles, Func<string, string?> read,
        HashSet<string> commonTemps)
    {
        var result = new List<Scene>();
        if (battles.Count == 0 || View(root, map) is not { } v || read($"data/maps/{map}/map.json") is not { } json)
        {
            return result;
        }

        var g = v.Grid;
        var names = Chokepoints.Behaviors(read("include/constants/metatile_behaviors.h")).GroupBy(b => b.Value).ToDictionary(b => b.Key, b => b.First().Key);

        // people who stay put (not ours, which move with each save)
        using var doc = JsonDocument.Parse(json);
        var people = doc.RootElement.TryGetProperty("object_events", out var objs) && objs.ValueKind == JsonValueKind.Array
            ? objs.EnumerateArray().Where(o => o.TryGetProperty("x", out _) && o.TryGetProperty("y", out _)
                    && !(o.TryGetProperty("script", out var s) && (s.GetString() ?? "").StartsWith(ScriptPrefix, StringComparison.Ordinal)))
                .Select(o => (X: o.GetProperty("x").GetInt32(), Y: o.GetProperty("y").GetInt32())).ToHashSet()
            : [];
        var warps = v.Warps.Select(w => (w.X, w.Y)).ToHashSet();
        bool Free(int x, int y) => g.Inside(x, y) && g.At(g.Collision, x, y) == 0 && !people.Contains((x, y)) && !warps.Contains((x, y));

        // temporary flags and variables nothing on this map (or shared) uses, from the top down
        var mapText = json + (read($"data/maps/{map}/scripts.inc") ?? "");
        var usedHere = TempName().Matches(mapText).Select(m => m.Value).ToHashSet(StringComparer.Ordinal);
        var flags = Enumerable.Range(1, 0x1F).Reverse().Select(i => $"FLAG_TEMP_{i:X}").Where(f => !commonTemps.Contains(f) && !usedHere.Contains(f)).ToList();
        var vars = Enumerable.Range(0, 0x10).Reverse().Select(i => $"VAR_TEMP_{i:X}").Where(f => !commonTemps.Contains(f) && !usedHere.Contains(f)).ToList();

        foreach (var (b, id) in battles)
        {
            if (flags.Count == 0 || vars.Count == 0)
            {
                break;
            }

            var (doorX, doorY, frontX, frontY) = (b.DoorX!.Value, b.DoorY!.Value, b.FrontX!.Value, b.FrontY!.Value);
            var (dx, dy) = (Math.Sign(frontX - doorX), Math.Sign(frontY - doorY));
            var door = g.Inside(doorX, doorY) && names.TryGetValue(g.Behavior(g.At(g.Metatile, doorX, doorY)), out var behavior) && Doors.Contains(behavior);

            // the player steps back (still facing the entrance) to make room: away from it, or else to a side
            string? playerStep = null;
            var player = (X: frontX, Y: frontY);
            foreach (var (sx, sy) in new[] { (dx, dy), (dy, dx), (-dy, -dx) })
            {
                if (Free(frontX + sx, frontY + sy))
                {
                    playerStep = Way(sx, sy);
                    player = (frontX + sx, frontY + sy);
                    break;
                }
            }

            // walking away: the shortest way from the front tile to somewhere off the screen (7 tiles left or right, 5 up
            // or down of the player), around the player and everyone else
            var walk = new List<string>();
            if (playerStep is not null)
            {
                var back = new Dictionary<(int X, int Y), (int X, int Y)>();
                var queue = new Queue<(int X, int Y)>();
                var start = (frontX, frontY);
                back[start] = start;
                queue.Enqueue(start);
                (int X, int Y)? end = null;
                while (queue.Count > 0 && end is null)
                {
                    var t = queue.Dequeue();
                    foreach (var (ox, oy) in new[] { (dx, dy), (1, 0), (-1, 0), (0, 1), (0, -1) })
                    {
                        var n = (X: t.X + ox, Y: t.Y + oy);
                        if (back.ContainsKey(n) || n == player || !Free(n.X, n.Y) || Math.Abs(n.X - frontX) + Math.Abs(n.Y - frontY) > 24)
                        {
                            continue;
                        }

                        back[n] = t;
                        if (Math.Abs(n.X - player.X) >= 8 || Math.Abs(n.Y - player.Y) >= 6)
                        {
                            end = n;
                            break;
                        }

                        queue.Enqueue(n);
                    }
                }

                for (var t = end; t is { } at && at != start; t = back[at])
                {
                    var from = back[at];
                    walk.Insert(0, "walk_" + Way(at.X - from.X, at.Y - from.Y));
                }
            }

            result.Add(new Scene(b, id, flags[0], vars[0], Way(dx, dy), door, playerStep, walk));
            flags.RemoveAt(0);
            vars.RemoveAt(0);
        }

        return result;
    }

    /// <summary>The person of a scene: in the entrance, looking out, hidden until the scene.</summary>
    private static MapPerson PersonOf(Scene s) =>
        new(ScriptPrefix + s.Battle.Id, s.Battle.Look, s.Battle.DoorX!.Value, s.Battle.DoorY!.Value, s.Battle.Elevation,
            FacingOf(s.Battle.FrontX!.Value - s.Battle.DoorX.Value, s.Battle.FrontY!.Value - s.Battle.DoorY.Value), 0, s.TempFlag);

    /// <summary>The scene script and its moves (<paramref name="won"/> adds what follows a first win: reward, clues).</summary>
    private static void SceneScript(StringBuilder script, Scene s, string label, Action<StringBuilder> won)
    {
        var b = s.Battle;
        var id = s.LocalId.ToString(CultureInfo.InvariantCulture);
        var (dx, dy) = (b.DoorX!.Value.ToString(CultureInfo.InvariantCulture), b.DoorY!.Value.ToString(CultureInfo.InvariantCulture));
        void L(string line) => script.Append('\t').Append(line).Append('\n');
        script.Append('\n').Append(label).Append("_Scene::\n");
        L("lockall");
        if (s.Required is { } required)
        {
            L($"goto_if_unset {required}, {label}_NotYet");
        }
        if (s.Door)
        {
            L($"opendoor {dx}, {dy}");
            L("waitdooranim");
        }

        L($"clearflag {s.TempFlag}");
        L($"addobject {id}");
        if (s.PlayerStep is not null)
        {
            L($"applymovement OBJ_EVENT_ID_PLAYER, {label}_StepBack");
            L("waitmovement 0");
            L($"applymovement {id}, {label}_ComeOut");
            L("waitmovement 0");
        }

        if (s.Door)
        {
            L($"closedoor {dx}, {dy}");
            L("waitdooranim");
        }

        L($"applymovement {id}, {label}_Notice");
        L("waitmovement 0");
        L($"msgbox {label}_Intro, MSGBOX_DEFAULT");
        L($"trainerbattle_no_intro {b.TrainerKey}, {label}_Defeat");
        won(script);
        L($"msgbox {label}_After, MSGBOX_DEFAULT");
        // walking off the screen, or (no dry way off, like Cinnabar's island) back inside through the entrance
        var goBack = s.WalkAway.Count == 0 && s.PlayerStep is not null;
        if (s.WalkAway.Count > 0)
        {
            L($"applymovement {id}, {label}_WalkAway");
            L("waitmovement 0");
        }
        else if (goBack)
        {
            if (s.Door)
            {
                L($"opendoor {dx}, {dy}");
                L("waitdooranim");
            }

            L($"applymovement {id}, {label}_GoBack");
            L("waitmovement 0");
        }

        L($"removeobject {id}");
        if (goBack && s.Door)
        {
            L($"closedoor {dx}, {dy}");
            L("waitdooranim");
        }
        L($"setflag {s.TempFlag}");
        L($"setvar {s.TempVar}, 1");
        L("releaseall");
        L("end");

        void Moves(string name, IEnumerable<string> steps)
        {
            script.Append('\n').Append(label).Append('_').Append(name).Append(":\n");
            foreach (var step in steps)
            {
                L(step);
            }

            L("step_end");
        }

        if (s.PlayerStep is { } step)
        {
            Moves("StepBack", ["lock_facing_direction", "walk_" + step, "unlock_facing_direction"]);
            Moves("ComeOut", ["walk_" + s.ComeOut]);
        }

        // too early: turned back at the entrance
        if (s.Required is { } requiredStep)
        {
            script.Append('\n').Append(label).Append("_NotYet:\n");
            L($"msgbox {label}_NotYetText, MSGBOX_DEFAULT");
            if (s.PlayerStep is not null)
            {
                L($"applymovement OBJ_EVENT_ID_PLAYER, {label}_TurnBack");
                L("waitmovement 0");
            }

            L("releaseall");
            L("end");
            var badge = System.Text.RegularExpressions.Regex.Match(requiredStep, @"^FLAG_BADGE0(\d)_GET$");
            var when = badge.Success ? $"after your {Ordinal(int.Parse(badge.Groups[1].Value, CultureInfo.InvariantCulture))} badge" : "later";
            script.Append('\n').Append(label).Append("_NotYetText:\n")
                .Append(CultureInfo.InvariantCulture, $"\t.string \"Not yet! You're not ready for this.\\nCome back {when}.$\"\n");
            if (s.PlayerStep is { } away)
            {
                Moves("TurnBack", ["walk_" + away]);
            }
        }

        Moves("Notice", ["emote_exclamation_mark"]);
        if (s.WalkAway.Count > 0)
        {
            Moves("WalkAway", s.WalkAway);
        }
        else if (goBack)
        {
            var back = s.ComeOut switch { "down" => "up", "up" => "down", "left" => "right", _ => "left" };
            Moves("GoBack", ["face_" + back, "walk_" + back]);
        }
    }

    /// <summary>
    /// The ON_TRANSITION script of a map with scenes: everyone waits hidden, and the step trigger is switched off for
    /// whoever is beaten (or gone: <paramref name="goneFlag"/>). Runs the map's own ON_TRANSITION afterwards.
    /// </summary>
    private static void TransitionScript(StringBuilder script, string map, IReadOnlyList<Scene> scenes, string? goneFlag, string? own)
    {
        var label = $"{ScriptPrefix}{map}_OnTransition";
        script.Append('\n').Append(label).Append("::\n");
        foreach (var s in scenes)
        {
            var gone = $"{ScriptPrefix}{s.Battle.Id}_Gone";
            script.Append(CultureInfo.InvariantCulture, $"\tsetflag {s.TempFlag}\n");
            script.Append(CultureInfo.InvariantCulture, $"\tcall_if_set TRAINER_FLAGS_START + {s.Battle.TrainerKey}, {gone}\n");
            if (goneFlag is not null)
            {
                script.Append(CultureInfo.InvariantCulture, $"\tcall_if_set {goneFlag}, {gone}\n");
            }
        }

        script.Append(own is null ? "\tend\n" : $"\tgoto {own}\n");
        foreach (var s in scenes)
        {
            script.Append('\n').Append(ScriptPrefix).Append(s.Battle.Id).Append("_Gone:\n")
                .Append(CultureInfo.InvariantCulture, $"\tsetvar {s.TempVar}, 1\n")
                .Append("\treturn\n");
        }
    }

    /// <summary>The map's own ON_TRANSITION script (not one the app added), or null.</summary>
    private static string? OwnTransition(string mapScripts, string map)
    {
        var table = Regex.Match(mapScripts, $@"^{Regex.Escape(map)}_MapScripts::[^\n]*\n((?:[^\n]*\n)*?)[ \t]*\.byte 0", RegexOptions.Multiline);
        if (!table.Success)
        {
            return null;
        }

        return table.Groups[1].Value.Split('\n')
            .Where(l => !l.Contains("@ Poke Studio", StringComparison.Ordinal))
            .Select(l => Regex.Match(l, @"map_script\s+MAP_SCRIPT_ON_TRANSITION\s*,\s*(\w+)"))
            .FirstOrDefault(m => m.Success)?.Groups[1].Value;
    }

    /// <summary>A map's scripts.inc with (or without) the table entry for the scenes' ON_TRANSITION script, put first.</summary>
    internal static string SceneTable(string mapScripts, string map, bool scenes)
    {
        var nl = CScanner.LineEnding(mapScripts);
        var clean = Regex.Replace(mapScripts, $@"^[^\n]*@ {SceneMark}[^\n]*\r?\n", "", RegexOptions.Multiline);
        var table = Regex.Match(clean, $@"^{Regex.Escape(map)}_MapScripts::[^\n]*\n", RegexOptions.Multiline);
        return !scenes || !table.Success
            ? clean
            : clean.Insert(table.Index + table.Length, $"\tmap_script MAP_SCRIPT_ON_TRANSITION, {ScriptPrefix}{map}_OnTransition @ {SceneMark}{nl}");
    }

    /// <summary>The map's step triggers with ours for the scenes (earlier ones of ours are already gone with the people).</summary>
    internal static string SceneTriggers(string json, IReadOnlyList<Scene> scenes)
    {
        json = Regex.Replace(json, @"(""coord_events"":\s*)\[\s*\]", "$1[]");
        if (scenes.Count == 0)
        {
            return json;
        }

        var nl = CScanner.LineEnding(json);
        var blocks = scenes.Select(s => string.Join(nl, new[]
        {
            "{",
            "  \"type\": \"trigger\",",
            $"  \"x\": {s.Battle.FrontX!.Value.ToString(CultureInfo.InvariantCulture)},",
            $"  \"y\": {s.Battle.FrontY!.Value.ToString(CultureInfo.InvariantCulture)},",
            "  \"elevation\": 0,",
            $"  \"var\": \"{s.TempVar}\",",
            "  \"var_value\": \"0\",",
            $"  \"script\": \"{ScriptPrefix}{s.Battle.Id}_Scene\"",
            "}",
        }.Select(l => "    " + l)));
        return AddToArray(json, "coord_events", string.Join("," + nl, blocks));
    }

    /// <summary>The json with <paramref name="text"/> (objects, comma-separated) added at the end of an array (made when missing).</summary>
    private static string AddToArray(string json, string key, string text)
    {
        var nl = CScanner.LineEnding(json);
        var at = json.IndexOf($"\"{key}\"", StringComparison.Ordinal);
        if (at < 0)
        {
            // no such list yet: add one before the map's last closing brace
            var close = json.LastIndexOf('}');
            var before = json.LastIndexOf(']', close) is var b and >= 0 && b > json.LastIndexOf('"', close) ? b : json.LastIndexOf('"', close);
            return json.Insert(before + 1, $",{nl}  \"{key}\": [{nl}{text}{nl}  ]");
        }

        var openArray = json.IndexOf('[', at);
        var closeArray = CScanner.FindMatching(json, openArray);
        var lastObject = json.LastIndexOf('}', closeArray);
        return lastObject > openArray
            ? json.Insert(lastObject + 1, "," + nl + text)
            : json.Remove(openArray + 1, closeArray - openArray - 1).Insert(openArray + 1, nl + text + nl + "  ");
    }

    [GeneratedRegex(@"\b(?:FLAG_TEMP_[0-9A-F]+|VAR_TEMP_[0-9A-F]+)\b")]
    private static partial Regex TempName();
}
