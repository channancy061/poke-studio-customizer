using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using PokeStudio.Core.SourceCode;

namespace PokeStudio.Modules.Expansion;

/// <summary>
/// Finds where the Pokémon Center helper can stand and adds it to the map's object events. Preferred: behind the counter
/// next to the nurse (the player talks across a counter tile). Otherwise a free floor tile that doesn't block any path,
/// door, sign or machine. Uses the map's own collision data and metatile behaviours.
/// </summary>
internal static partial class CenterPlacement
{
    public const string HelperScript = "PokeStudio_EventScript_CenterHelper";
    public const string SpecialistScript = "PokeStudio_EventScript_Specialist";

    /// <summary>Every Poké Mart map (data/maps/…_Mart/map.json; department stores aren't Marts).</summary>
    public static IEnumerable<string> MartMaps(string root)
    {
        var maps = Path.Combine(root, "data", "maps");
        if (!Directory.Exists(maps))
        {
            yield break;
        }

        foreach (var dir in Directory.GetDirectories(maps).Order(StringComparer.Ordinal))
        {
            if (Path.GetFileName(dir).EndsWith("_Mart", StringComparison.Ordinal) && File.Exists(Path.Combine(dir, "map.json")))
            {
                yield return $"data/maps/{Path.GetFileName(dir)}/map.json";
            }
        }
    }

    /// <summary>Every Pokémon Center ground floor map (data/maps/…PokemonCenter_1F…/map.json).</summary>
    public static IEnumerable<string> CenterMaps(string root)
    {
        var maps = Path.Combine(root, "data", "maps");
        if (!Directory.Exists(maps))
        {
            yield break;
        }

        foreach (var dir in Directory.GetDirectories(maps).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(dir);
            if (name.Contains("PokemonCenter_1F", StringComparison.Ordinal) && File.Exists(Path.Combine(dir, "map.json")))
            {
                yield return $"data/maps/{name}/map.json";
            }
        }
    }

    internal sealed record Grid(int Width, int Height, int[] Metatile, int[] Collision, int[] Elevation, Func<int, int> Behavior)
    {
        public bool Inside(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height;
        public int At(int[] a, int x, int y) => a[(y * Width) + x];
    }

    /// <summary>Reads a layout's blocks and the behaviour of each metatile (null when something is missing).</summary>
    internal static Grid? LoadGrid(Func<string, string?> read, Func<string, byte[]?> readBytes, string layoutId)
    {
        if (read("data/layouts/layouts.json") is not { } layoutsJson)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(layoutsJson);
        var layout = doc.RootElement.GetProperty("layouts").EnumerateArray()
            .FirstOrDefault(l => l.TryGetProperty("id", out var id) && id.GetString() == layoutId);
        if (layout.ValueKind != JsonValueKind.Object || readBytes(layout.GetProperty("blockdata_filepath").GetString()!) is not { } blocks)
        {
            return null;
        }

        var w = layout.GetProperty("width").GetInt32();
        var h = layout.GetProperty("height").GetInt32();
        if (blocks.Length < w * h * 2)
        {
            return null;
        }

        var meta = new int[w * h];
        var coll = new int[w * h];
        var elev = new int[w * h];
        for (var i = 0; i < w * h; i++)
        {
            var v = blocks[i * 2] | (blocks[(i * 2) + 1] << 8);
            meta[i] = v & 0x3FF;
            coll[i] = (v >> 10) & 3;
            elev[i] = v >> 12;
        }

        var primary = Attributes(read, readBytes, layout.GetProperty("primary_tileset").GetString()!);
        var secondary = Attributes(read, readBytes, layout.GetProperty("secondary_tileset").GetString()!);
        if (primary is null || secondary is null)
        {
            return null;
        }

        int Behavior(int metatile) => metatile < primary.Length ? primary[metatile]
            : metatile - primary.Length < secondary.Length ? secondary[metatile - primary.Length] : 0;
        return new Grid(w, h, meta, coll, elev, Behavior);
    }

    [GeneratedRegex(@"const u(16|32) (gMetatileAttributes_\w+)\[\]\s*=\s*INCBIN_U(?:16|32)\(""([^""]+)""\)")]
    private static partial Regex AttributeFile();

    /// <summary>Metatile behaviours of a tileset (u16 attributes: low 8 bits; u32: low 9 bits).</summary>
    private static int[]? Attributes(Func<string, string?> read, Func<string, byte[]?> readBytes, string tileset)
    {
        var headers = read("src/data/tilesets/headers.h");
        var metatiles = read("src/data/tilesets/metatiles.h");
        if (headers is null || metatiles is null)
        {
            return null;
        }

        var at = headers.IndexOf($"{tileset} =", StringComparison.Ordinal);
        if (at < 0)
        {
            return null;
        }

        var attr = Regex.Match(headers[at..], @"\.metatileAttributes\s*=\s*(\w+)");
        var file = AttributeFile().Matches(metatiles).FirstOrDefault(m => m.Groups[2].Value == attr.Groups[1].Value);
        if (!attr.Success || file is null || readBytes(file.Groups[3].Value) is not { } bytes)
        {
            return null;
        }

        var wide = file.Groups[1].Value == "32";
        var size = wide ? 4 : 2;
        var result = new int[bytes.Length / size];
        for (var i = 0; i < result.Length; i++)
        {
            var v = wide ? BitConverter.ToInt32(bytes, i * size) : BitConverter.ToUInt16(bytes, i * size);
            result[i] = v & (wide ? 0x1FF : 0xFF);
        }

        return result;
    }

    internal sealed record Spot(int X, int Y, int Elevation, bool BehindCounter, string Facing = "DOWN");

    private sealed record Thing(int X, int Y);

    /// <summary>Where the helper can stand on this map (next to the nurse), or null.</summary>
    internal static Spot? Find(string mapJson, Grid g) => Find(mapJson, g, gfx => gfx.Contains("NURSE", StringComparison.Ordinal));

    /// <summary>
    /// Where someone can stand next to the person whose sprite matches <paramref name="anchor"/> (the nurse, a Mart
    /// clerk): behind the same counter, which is in front of them (the way they face), or else a free tile on the
    /// customers' side. Null when there's no such person or room.
    /// </summary>
    internal static Spot? Find(string mapJson, Grid g, Func<string, bool> anchor)
    {
        using var doc = JsonDocument.Parse(mapJson);
        var root = doc.RootElement;
        List<Thing> Things(string name) => root.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array
            ? arr.EnumerateArray().Where(o => o.TryGetProperty("x", out _) && o.TryGetProperty("y", out _))
                .Select(o => new Thing(o.GetProperty("x").GetInt32(), o.GetProperty("y").GetInt32())).ToList()
            : [];

        var objects = Things("object_events");
        var warps = Things("warp_events");
        var signs = Things("bg_events");
        var triggers = Things("coord_events");
        var nurse = root.TryGetProperty("object_events", out var objs)
            ? objs.EnumerateArray().FirstOrDefault(o => o.TryGetProperty("graphics_id", out var gfx) && anchor(gfx.GetString()!))
            : default;
        if (nurse.ValueKind != JsonValueKind.Object || warps.Count == 0)
        {
            return null;
        }

        var nx = nurse.GetProperty("x").GetInt32();
        var ny = nurse.GetProperty("y").GetInt32();
        var elevation = nurse.TryGetProperty("elevation", out var e) ? e.GetInt32() : 3;
        // the counter is in front of them: the nurse looks down, a Mart clerk to the right
        var movement = nurse.TryGetProperty("movement_type", out var mt) ? mt.GetString() ?? "" : "";
        var (dx, dy, facing) = movement.EndsWith("_RIGHT", StringComparison.Ordinal) ? (1, 0, "RIGHT")
            : movement.EndsWith("_LEFT", StringComparison.Ordinal) ? (-1, 0, "LEFT")
            : movement.EndsWith("_UP", StringComparison.Ordinal) ? (0, -1, "UP") : (0, 1, "DOWN");
        var (px, py) = (dy, dx); // along the counter
        bool Occupied(int x, int y) => objects.Any(o => o.X == x && o.Y == y) || warps.Any(o => o.X == x && o.Y == y)
            || signs.Any(o => o.X == x && o.Y == y) || triggers.Any(o => o.X == x && o.Y == y);
        bool Walkable(int x, int y) => g.Inside(x, y) && g.At(g.Collision, x, y) == 0;

        // 1. behind the counter: the tile in front behaves like the one in front of them (a counter)
        if (g.Inside(nx + dx, ny + dy))
        {
            var counter = g.Behavior(g.At(g.Metatile, nx + dx, ny + dy));
            foreach (var k in Enumerable.Range(1, Math.Max(g.Width, g.Height)).SelectMany(k => new[] { k, -k }))
            {
                var (x, y) = (nx + (k * px), ny + (k * py));
                if (g.Inside(x, y) && Walkable(x, y) && !Occupied(x, y) && g.Inside(x + (2 * dx), y + (2 * dy))
                    && g.At(g.Collision, x + dx, y + dy) != 0 && counter != 0 && g.Behavior(g.At(g.Metatile, x + dx, y + dy)) == counter
                    && Walkable(x + (2 * dx), y + (2 * dy)) && !Occupied(x + (2 * dx), y + (2 * dy)))
                {
                    return new Spot(x, y, elevation, true, facing);
                }
            }
        }

        // 2. a floor tile that keeps every reachable tile reachable and doesn't stand in front of anything
        var start = warps[0];
        var before = Reachable(g, start.X, start.Y, -1, -1);
        bool NextToSomething(int x, int y)
        {
            foreach (var (dx, dy) in new[] { (0, 1), (0, -1), (1, 0), (-1, 0) })
            {
                var (ax, ay) = (x + dx, y + dy);
                if (!g.Inside(ax, ay))
                {
                    continue;
                }

                // machines, counters, shelves and signs need their front tile; warps need room around them
                if ((g.At(g.Collision, ax, ay) != 0 && g.Behavior(g.At(g.Metatile, ax, ay)) != 0)
                    || warps.Any(w => w.X == ax && w.Y == ay) || signs.Any(s => s.X == ax && s.Y == ay) || objects.Any(o => o.X == ax && o.Y == ay))
                {
                    return true;
                }
            }

            return false;
        }

        // on the customers' side of the counter, a little to the side
        var (tx, ty) = (nx + (2 * dx) + (4 * px), ny + (2 * dy) + (4 * py));
        var candidates = before
            .Where(p => !Occupied(p.X, p.Y) && !NextToSomething(p.X, p.Y) && ((p.X - nx) * dx) + ((p.Y - ny) * dy) > 0)
            .OrderBy(p => Math.Abs(p.X - tx) + Math.Abs(p.Y - ty))
            .ThenBy(p => p.Y).ThenBy(p => p.X);
        foreach (var p in candidates)
        {
            var after = Reachable(g, start.X, start.Y, p.X, p.Y);
            var lost = before.Count(t => !(t.X == p.X && t.Y == p.Y) && !after.Contains(t));
            var touchable = new[] { (0, 1), (0, -1), (1, 0), (-1, 0) }.Any(d => after.Contains((p.X + d.Item1, p.Y + d.Item2)));
            if (lost == 0 && touchable)
            {
                return new Spot(p.X, p.Y, elevation, false, facing);
            }
        }

        return null;
    }

    private static HashSet<(int X, int Y)> Reachable(Grid g, int sx, int sy, int blockX, int blockY)
    {
        var seen = new HashSet<(int, int)>();
        var queue = new Queue<(int X, int Y)>();
        queue.Enqueue((sx, sy));
        seen.Add((sx, sy));
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

    /// <summary>The map.json without the object that runs <paramref name="script"/> (unchanged when there is none).</summary>
    internal static string RemoveObject(string mapJson, string script)
    {
        var at = mapJson.IndexOf($"\"script\": \"{script}\"", StringComparison.Ordinal);
        if (at < 0)
        {
            return mapJson;
        }

        var open = mapJson.LastIndexOf('{', at);
        var close = mapJson.IndexOf('}', at);
        var before = mapJson.LastIndexOf(',', open);
        var prevEnd = Math.Max(mapJson.LastIndexOf('}', open), mapJson.LastIndexOf('[', open));
        if (before > prevEnd)
        {
            return mapJson.Remove(before, close + 1 - before); // ", { … }" after another object
        }

        var after = mapJson.IndexOf(',', close);
        var arrayEnd = mapJson.IndexOf(']', close);
        return after >= 0 && after < arrayEnd
            ? mapJson.Remove(open, mapJson.IndexOf('{', after) - open) // the first of several
            : mapJson.Remove(open, close + 1 - open);                 // the only one
    }

    /// <summary>The map.json with the helper added at the end of its object events (unchanged if it is already there).</summary>
    internal static string AddHelper(string mapJson, Spot spot, string graphicsId, string script = HelperScript)
    {
        if (mapJson.Contains(script, StringComparison.Ordinal))
        {
            return mapJson;
        }

        var nl = CScanner.LineEnding(mapJson);
        var key = mapJson.IndexOf("\"object_events\"", StringComparison.Ordinal);
        var open = key < 0 ? -1 : mapJson.IndexOf('[', key);
        if (open < 0)
        {
            return mapJson;
        }

        var close = CScanner.FindMatching(mapJson, open);
        var lastObject = mapJson.LastIndexOf('}', close);
        var indent = "    ";
        var lines = new[]
        {
            "{",
            $"  \"graphics_id\": \"{graphicsId}\",",
            $"  \"x\": {spot.X.ToString(CultureInfo.InvariantCulture)},",
            $"  \"y\": {spot.Y.ToString(CultureInfo.InvariantCulture)},",
            $"  \"elevation\": {spot.Elevation.ToString(CultureInfo.InvariantCulture)},",
            $"  \"movement_type\": \"MOVEMENT_TYPE_FACE_{spot.Facing}\",",
            "  \"movement_range_x\": 0,",
            "  \"movement_range_y\": 0,",
            "  \"trainer_type\": \"TRAINER_TYPE_NONE\",",
            "  \"trainer_sight_or_berry_tree_id\": \"0\",",
            $"  \"script\": \"{script}\",",
            "  \"flag\": \"0\"",
            "}",
        };
        var block = string.Join(nl, lines.Select(l => indent + l));
        return lastObject > open
            ? mapJson.Insert(lastObject + 1, "," + nl + block)
            : mapJson.Remove(open + 1, close - open - 1).Insert(open + 1, nl + block + nl + "  ");
    }
}
