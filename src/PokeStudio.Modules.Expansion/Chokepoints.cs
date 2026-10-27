using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using PokeStudio.Core.SourceCode;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// Hard and Extra Hard: route trainers stand on chokepoints. A chokepoint is a straight row or column of floor that the
/// player can't get around: taking it away splits the route's entrances apart. The trainer stands at one end and looks
/// along it, so whoever passes crosses their line of sight. The moves are made by each route's ON_TRANSITION script
/// (setobjectxyperm), only on Hard and up, so Easy and Normal keep the original places.
/// </summary>
internal static partial class Chokepoints
{
    public const string Mark = "Poke Studio: chokepoints";
    public const string Native = "PokeStudio_HardTrainers";
    private const int SightAcross = 7, SightAlong = 5; // what Hard trainers see: as far as the screen shows
    private const int Spacing = 5;                     // tiles between two trainers' lines
    private const int MaxShift = 15;                   // steps a trainer may move from its own place

    /// <summary>A trainer moved: local id (1-based object number), new tile and the way they look.</summary>
    internal sealed record Move(int LocalId, string Script, int FromX, int FromY, int X, int Y, string Facing);

    /// <summary>A route's plan: the moves, and the trainers that keep their place (no chokepoint for them).</summary>
    internal sealed record RoutePlan(string Map, List<Move> Moves, List<string> Kept)
    {
        public string Why { get; init; } = "";

        /// <summary>Trainers whose own place already can't be walked past (they stay).</summary>
        public List<string> AlreadyInTheWay { get; init; } = [];
    }

    private sealed record Person(int LocalId, int X, int Y, int Elevation, string TrainerType, string Script, string Flag, string Graphics);

    private sealed record Spot(List<(int X, int Y)> Tiles, bool Horizontal, int Separated);

    /// <summary>
    /// The route maps (MAP_TYPE_ROUTE) of a project; with <paramref name="indoors"/> also caves and buildings (Rocket
    /// Hideout, Silph Co., Mt. Moon), never a Gym, whose trainers are its puzzle.
    /// </summary>
    public static IEnumerable<string> Routes(string root, bool indoors = false)
    {
        var dir = Path.Combine(root, "data", "maps");
        if (!Directory.Exists(dir))
        {
            yield break;
        }

        foreach (var folder in Directory.GetDirectories(dir).Order(StringComparer.Ordinal))
        {
            var file = Path.Combine(folder, "map.json");
            if (File.Exists(file) && File.ReadAllText(file) is var json
                && (RouteType().IsMatch(json) || indoors && IndoorType().IsMatch(json) && !Path.GetFileName(folder).Contains("Gym", StringComparison.Ordinal)))
            {
                yield return Path.GetFileName(folder);
            }
        }
    }

    /// <summary>Metatile behaviour numbers by name (Emerald's #defines or FireRed's enum).</summary>
    internal static Dictionary<string, int> Behaviors(string? header)
    {
        var result = new Dictionary<string, int>(StringComparer.Ordinal);
        if (header is null)
        {
            return result;
        }

        foreach (Match m in Regex.Matches(header, @"^#define\s+(MB_\w+)\s+(0x[0-9A-Fa-f]+|\d+)", RegexOptions.Multiline))
        {
            var v = m.Groups[2].Value;
            result[m.Groups[1].Value] = v.StartsWith("0x", StringComparison.Ordinal) ? int.Parse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : int.Parse(v, CultureInfo.InvariantCulture);
        }

        var open = header.IndexOf("enum", StringComparison.Ordinal);
        if (result.Count == 0 && open >= 0 && header.IndexOf('{', open) is var brace and > 0)
        {
            var next = 0;
            foreach (Match m in Regex.Matches(header[brace..CScanner.FindMatching(header, brace)], @"\b(MB_\w+)\s*(?:=\s*(0x[0-9A-Fa-f]+|\d+))?\s*,"))
            {
                if (m.Groups[2].Success)
                {
                    var v = m.Groups[2].Value;
                    next = v.StartsWith("0x", StringComparison.Ordinal) ? int.Parse(v[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture) : int.Parse(v, CultureInfo.InvariantCulture);
                }

                result[m.Groups[1].Value] = next++;
            }
        }

        return result;
    }

    /// <summary>Where a route's trainers go on Hard (null when the map can't be read).</summary>
    internal static RoutePlan? PlanRoute(string map, Func<string, string?> read, Func<string, byte[]?> readBytes, IReadOnlyDictionary<string, int> behaviors, bool picture = false)
    {
        if (read($"data/maps/{map}/map.json") is not { } json)
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
        var people = new List<Person>();
        if (rootEl.TryGetProperty("object_events", out var objs) && objs.ValueKind == JsonValueKind.Array)
        {
            var id = 0;
            foreach (var o in objs.EnumerateArray())
            {
                id++;
                if (o.TryGetProperty("x", out var ox) && o.TryGetProperty("y", out var oy))
                {
                    people.Add(new Person(id, ox.GetInt32(), oy.GetInt32(), o.TryGetProperty("elevation", out var e) ? e.GetInt32() : 3,
                        Str(o, "trainer_type"), Str(o, "script"), Str(o, "flag"), Str(o, "graphics_id")));
                }
            }
        }

        // trainer scripts are in the map's own file (Emerald) or all together in trainers.inc (FireRed)
        var script = (read($"data/maps/{map}/scripts.inc") ?? "") + "\n" + (read("data/scripts/trainers.inc") ?? "");
        bool Battles(Person p)
        {
            var at = script.IndexOf(p.Script + "::", StringComparison.Ordinal);
            if (at < 0)
            {
                return false;
            }

            var end = script.IndexOf("\n\tend", at, StringComparison.Ordinal) is var e and >= 0 ? e : script.Length;
            var body = script[at..end];
            return Regex.IsMatch(body, @"\btrainerbattle_single\b|\btrainerbattle\s+TRAINER_BATTLE_SINGLE\b") && !body.Contains("_double", StringComparison.Ordinal);
        }

        // regular one-on-one trainers that are there from the start (not the app's own story trainers, not a pair of twins);
        // a FLAG_HIDE_ one leaves after the story (Rocket Hideout's grunts), which moving doesn't change
        var trainers = people.Where(p => p.TrainerType is "TRAINER_TYPE_NORMAL" or "TRAINER_TYPE_SEE_ALL_DIRECTIONS"
                && (p.Flag is "0" or "" || p.Flag.StartsWith("FLAG_HIDE_", StringComparison.Ordinal)) && !p.Script.StartsWith(StoryWriter.ScriptPrefix, StringComparison.Ordinal) && Battles(p)
                && !people.Any(q => q != p && q.Script == p.Script))
            .ToList();
        if (trainers.Count == 0)
        {
            return new RoutePlan(map, [], []);
        }

        var water = behaviors.Where(b => b.Key.Contains("WATER", StringComparison.Ordinal) && b.Key is not ("MB_SHALLOW_WATER" or "MB_PUDDLE")
            || b.Key == "MB_WATERFALL").Select(b => b.Value).ToHashSet();
        var ledges = behaviors.Where(b => b.Key.StartsWith("MB_JUMP_", StringComparison.Ordinal)).Select(b => b.Value).ToHashSet();
        var warps = Things(rootEl, "warp_events");
        var taken = Things(rootEl, "bg_events").Concat(Things(rootEl, "coord_events")).Concat(warps).ToHashSet();
        // people who stay put block the way; trees to cut, rocks to smash, boulders to push and item balls don't (the player clears them)
        var fixedPeople = people.Where(p => !trainers.Contains(p) && !FieldObstacle().IsMatch(p.Graphics)).Select(p => (p.X, p.Y)).ToHashSet();

        // a ledge is a wall the player can jump down (the game checks for the jump when the way is blocked), so it counts as floor
        bool Ledge(int x, int y) => g.Inside(x, y) && ledges.Contains(g.Behavior(g.At(g.Metatile, x, y)));
        bool Floor(int x, int y) => g.Inside(x, y) && (g.At(g.Collision, x, y) == 0 || Ledge(x, y)) && !water.Contains(g.Behavior(g.At(g.Metatile, x, y))) && !fixedPeople.Contains((x, y));

        // the ways in and out: each side the route connects on, and each warp
        var exits = new List<HashSet<(int X, int Y)>>();
        if (rootEl.TryGetProperty("connections", out var conns) && conns.ValueKind == JsonValueKind.Array)
        {
            foreach (var side in conns.EnumerateArray().Select(c => Str(c, "direction")).Distinct(StringComparer.Ordinal))
            {
                var edge = side switch
                {
                    "up" => Enumerable.Range(0, g.Width).Select(x => (x, 0)),
                    "down" => Enumerable.Range(0, g.Width).Select(x => (x, g.Height - 1)),
                    "left" => Enumerable.Range(0, g.Height).Select(y => (0, y)),
                    "right" => Enumerable.Range(0, g.Height).Select(y => (g.Width - 1, y)),
                    _ => [],
                };
                var tiles = edge.Where(t => Floor(t.Item1, t.Item2)).ToHashSet();
                if (tiles.Count > 0)
                {
                    exits.Add(tiles);
                }
            }
        }

        foreach (var w in warps)
        {
            exits.Add([w]);
        }

        var drawing = new StringBuilder();
        if (picture)
        {
            // for checking a route by eye: . floor, # wall, ~ water, ^ ledge, T trainer, o other person, E way in
            for (var y = 0; y < g.Height; y++)
            {
                drawing.AppendLine();
                for (var x = 0; x < g.Width; x++)
                {
                    drawing.Append(exits.Any(e => e.Contains((x, y))) ? 'E' : trainers.Any(t => t.X == x && t.Y == y) ? 'T' : fixedPeople.Contains((x, y)) ? 'o'
                        : g.Inside(x, y) && water.Contains(g.Behavior(g.At(g.Metatile, x, y))) ? '~' : !Floor(x, y) ? '#' : Ledge(x, y) ? '^' : '.');
                }
            }
        }

        // pairs of ways that are joined now: a chokepoint splits at least one of them
        var pairs = new List<(int A, int B)>();
        for (var a = 0; a < exits.Count; a++)
        {
            var from = Reach(g, exits[a], Floor, warps, null);
            for (var b = a + 1; b < exits.Count; b++)
            {
                if (exits[b].Any(from.Contains))
                {
                    pairs.Add((a, b));
                }
            }
        }

        int Splits(IReadOnlySet<(int X, int Y)> cut) => pairs.Count(p => !exits[p.B].Any(Reach(g, exits[p.A], Floor, warps, cut).Contains));
        var reachAll = exits.Count > 0 ? Reach(g, exits[0], Floor, warps, null) : [];
        bool ClosesAlone((int X, int Y) t)
        {
            // a trainer stays there even after the battle: their own tile must never close the way
            var after = exits.Count > 0 ? Reach(g, exits[0], Floor, warps, new HashSet<(int X, int Y)> { t }) : [];
            return reachAll.Any(r => r != t && !after.Contains(r));
        }

        // what a Hard trainer standing on a tile sees: straight out in all four directions, as far as the screen shows,
        // until a wall, a ledge (the player jumps over it) or a change of height
        HashSet<(int X, int Y)> Seen(int x, int y)
        {
            var seen = new HashSet<(int X, int Y)> { (x, y) };
            var elevation = g.At(g.Elevation, x, y);
            foreach (var (dx, dy, range) in new[] { (1, 0, SightAcross), (-1, 0, SightAcross), (0, 1, SightAlong), (0, -1, SightAlong) })
            {
                for (var k = 1; k <= range; k++)
                {
                    var (sx, sy) = (x + (dx * k), y + (dy * k));
                    if (!Floor(sx, sy) || Ledge(sx, sy) || g.At(g.Elevation, sx, sy) != elevation)
                    {
                        break;
                    }

                    seen.Add((sx, sy));
                }
            }

            return seen;
        }

        // trainers already standing where nobody gets past them stay where they are
        var inTheWay = trainers.Where(t => pairs.Count > 0 && Floor(t.X, t.Y) && Splits(Seen(t.X, t.Y)) > 0).ToList();
        var toPlace = trainers.Except(inTheWay).ToList();

        // spots: a floor tile whose watched lines can't be got around, inside a straight run of floor (bounded at both ends)
        var spots = new List<Spot>();
        if (pairs.Count > 0 && toPlace.Count > 0)
        {
            bool SpotTile(int x, int y) => Floor(x, y) && !Ledge(x, y) && !taken.Contains((x, y))
                && !exits.Any(e => e.Any(t => Math.Abs(t.X - x) + Math.Abs(t.Y - y) <= 2)); // not where the player walks in
            var elevations = toPlace.Select(t => t.Elevation).ToHashSet();
            foreach (var horizontal in new[] { true, false })
            {
                var range = horizontal ? SightAcross : SightAlong;
                for (var a = 0; a < (horizontal ? g.Height : g.Width); a++)
                {
                    var run = new List<(int X, int Y)>();
                    for (var b = 0; b <= (horizontal ? g.Width : g.Height); b++)
                    {
                        var (x, y) = horizontal ? (b, a) : (a, b);
                        if (b < (horizontal ? g.Width : g.Height) && SpotTile(x, y))
                        {
                            run.Add((x, y));
                            continue;
                        }

                        // the trainer stands inside the run and watches both ways: up to the range on each side
                        if (run.Count >= 2 && run.Count <= (2 * range) + 1 && elevations.Contains(g.At(g.Elevation, run[0].X, run[0].Y))
                            && run.All(t => g.At(g.Elevation, t.X, t.Y) == g.At(g.Elevation, run[0].X, run[0].Y)))
                        {
                            var split = Splits(run.ToHashSet());
                            if (split > 0)
                            {
                                spots.Add(new Spot([.. run], horizontal, split));
                            }
                        }

                        run.Clear();
                    }
                }
            }
        }

        // each trainer takes the best free spot near its own place, so it stays in its own part of the route: a strong
        // trainer from a corner reached later in the game (FireRed's Lv 31 Crissy on Route 4) never ends up on the way in
        var start = exits.Count > 0 ? exits[0] : [];
        var distance = Distances(g, start, Floor, warps);
        int Along((int X, int Y) t) => distance.GetValueOrDefault(t, int.MaxValue);
        var moves = new List<Move>();
        var kept = new List<string>();
        var occupied = new HashSet<(int X, int Y)>();
        var chosen = new List<Spot>();
        foreach (var t in toPlace.OrderBy(t => Along((t.X, t.Y))))
        {
            var near = Distances(g, [(t.X, t.Y)], Floor, warps);
            int Steps((int X, int Y) tile) => near.GetValueOrDefault(tile, int.MaxValue);
            Move? move = null;
            var candidates = spots
                .Where(sp => t.Elevation == g.At(g.Elevation, sp.Tiles[0].X, sp.Tiles[0].Y) && sp.Tiles.Any(x => Steps(x) <= MaxShift)
                    && chosen.All(c => Gap(c.Tiles, sp.Tiles) >= Spacing) && inTheWay.All(w => Gap([(w.X, w.Y)], sp.Tiles) >= Spacing))
                .OrderByDescending(sp => sp.Separated).ThenBy(sp => sp.Tiles.Min(Steps)).ThenBy(sp => sp.Tiles.Count);
            foreach (var spot in candidates)
            {
                // a tile inside the run that sees both ends, nearest the trainer's own place; never closing the way
                var range = spot.Horizontal ? SightAcross : SightAlong;
                var n = spot.Tiles.Count;
                foreach (var i in Enumerable.Range(0, n).Where(i => i <= range && n - 1 - i <= range && Steps(spot.Tiles[i]) <= MaxShift)
                             .OrderBy(i => Steps(spot.Tiles[i])))
                {
                    var tile = spot.Tiles[i];
                    if (occupied.Contains(tile) || people.Any(p => p != t && p.X == tile.X && p.Y == tile.Y) || ClosesAlone(tile))
                    {
                        continue;
                    }

                    // looking along the longer side (Hard trainers see every way anyway)
                    var facing = spot.Horizontal ? (n - 1 - i >= i ? "RIGHT" : "LEFT") : (n - 1 - i >= i ? "DOWN" : "UP");
                    move = new Move(t.LocalId, t.Script, t.X, t.Y, tile.X, tile.Y, facing);
                    break;
                }

                if (move is not null)
                {
                    chosen.Add(spot);
                    break;
                }
            }

            if (move is null)
            {
                kept.Add(t.Script);
            }
            else
            {
                moves.Add(move);
                occupied.Add((move.X, move.Y));
            }
        }

        return new RoutePlan(map, moves, kept)
        {
            AlreadyInTheWay = inTheWay.Select(t => t.Script).ToList(),
            Why = $"{exits.Count} ways in, {pairs.Count} joined pairs, {spots.Count} spots, {chosen.Count} chosen, {inTheWay.Count} already in the way{drawing}",
        };
    }

    private static int Gap(IReadOnlyList<(int X, int Y)> a, IReadOnlyList<(int X, int Y)> b) =>
        a.SelectMany(p => b.Select(q => Math.Abs(p.X - q.X) + Math.Abs(p.Y - q.Y))).Min();

    private static List<(int X, int Y)> Things(JsonElement root, string name) =>
        root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(o => o.TryGetProperty("x", out _) && o.TryGetProperty("y", out _))
                .Select(o => (o.GetProperty("x").GetInt32(), o.GetProperty("y").GetInt32())).ToList()
            : [];

    // floor reachable from a way in (warp tiles count as floor), without the blocked tiles
    private static HashSet<(int X, int Y)> Reach(CenterPlacement.Grid g, IEnumerable<(int X, int Y)> from, Func<int, int, bool> floor,
        IReadOnlyCollection<(int X, int Y)> warps, IReadOnlySet<(int X, int Y)>? blocked) =>
        Distances(g, from, floor, warps, blocked).Keys.ToHashSet();

    private static Dictionary<(int X, int Y), int> Distances(CenterPlacement.Grid g, IEnumerable<(int X, int Y)> from, Func<int, int, bool> floor,
        IReadOnlyCollection<(int X, int Y)> warps, IReadOnlySet<(int X, int Y)>? blocked = null)
    {
        bool Open((int X, int Y) t) => (floor(t.X, t.Y) || warps.Contains(t)) && blocked?.Contains(t) != true;
        var seen = new Dictionary<(int X, int Y), int>();
        var queue = new Queue<(int X, int Y)>();
        foreach (var t in from.Where(Open))
        {
            if (seen.TryAdd(t, 0))
            {
                queue.Enqueue(t);
            }
        }

        while (queue.Count > 0)
        {
            var t = queue.Dequeue();
            foreach (var (dx, dy) in new[] { (0, 1), (0, -1), (1, 0), (-1, 0) })
            {
                var n = (t.X + dx, t.Y + dy);
                if (Open(n) && seen.TryAdd(n, seen[t] + 1))
                {
                    queue.Enqueue(n);
                }
            }
        }

        return seen;
    }

    /// <summary>
    /// A route's scripts.inc with the moves (an ON_TRANSITION entry put first in the map's script table, and a script
    /// that runs the map's own ON_TRANSITION afterwards), or as it was without them.
    /// </summary>
    internal static string Script(string text, string map, IReadOnlyList<Move> moves)
    {
        var nl = CScanner.LineEnding(text);
        var clean = Regex.Replace(text, $@"^[^\n]*@ {Mark}[^\n]*\r?\n", "", RegexOptions.Multiline);
        if (clean != text)
        {
            clean = clean.TrimEnd('\r', '\n') + nl; // the blank line the moves were written after
        }
        if (moves.Count == 0)
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
        var label = $"PokeStudio_Choke_{map}";
        var done = own.Success ? $"goto {own.Groups[1].Value}" : "end";
        var block = new StringBuilder();
        block.Append(nl).Append(label).Append(":: @ ").Append(Mark).Append(nl)
            .Append("\tcallnative ").Append(Native).Append(" @ ").Append(Mark).Append(nl)
            .Append("\tgoto_if_eq VAR_RESULT, FALSE, ").Append(label).Append("_Done @ ").Append(Mark).Append(nl);
        foreach (var m in moves)
        {
            block.Append(CultureInfo.InvariantCulture, $"\tsetobjectxyperm {m.LocalId}, {m.X}, {m.Y} @ {Mark}").Append(nl)
                .Append(CultureInfo.InvariantCulture, $"\tsetobjectmovementtype {m.LocalId}, MOVEMENT_TYPE_FACE_{m.Facing} @ {Mark}").Append(nl);
        }

        block.Append(label).Append("_Done: @ ").Append(Mark).Append(nl)
            .Append('\t').Append(done).Append(" @ ").Append(Mark).Append(nl);
        var withEntry = clean.Insert(table.Index + table.Length, $"\tmap_script MAP_SCRIPT_ON_TRANSITION, {label} @ {Mark}{nl}");
        return withEntry.TrimEnd('\r', '\n') + nl + block;
    }

    [GeneratedRegex("CUT_TREE|BREAKABLE_ROCK|ROCK_SMASH|BOULDER|ITEM_BALL")]
    private static partial Regex FieldObstacle();

    [GeneratedRegex("\"map_type\"\\s*:\\s*\"MAP_TYPE_ROUTE\"")]
    private static partial Regex RouteType();

    [GeneratedRegex("\"map_type\"\\s*:\\s*\"MAP_TYPE_(INDOOR|UNDERGROUND)\"")]
    private static partial Regex IndoorType();
}
