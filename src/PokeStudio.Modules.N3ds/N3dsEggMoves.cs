using System.Buffers.Binary;
using System.Text;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Validation;

namespace PokeStudio.Modules.N3ds;

/// <summary>Egg moves in the 3DS games: an archive with one entry per Pokémon (count, then moves).</summary>
internal static class N3dsEggMoves
{
    private static ushort W(byte[] b, int i) => BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(i));

    /// <summary>The archive whose entry for Bulbasaur is a count followed by that many moves, including Skull Bash.</summary>
    public static string? Locate(IReadOnlyDictionary<string, Garc> garcs) =>
        garcs.OrderBy(kv => kv.Key, StringComparer.Ordinal).Where(kv => IsEggMoves(kv.Value)).Select(kv => kv.Key).FirstOrDefault();

    private static bool IsEggMoves(Garc g) =>
        g.Count is >= 700 and <= 1200 && g[1] is { Length: >= 4 } f && W(f, 0) <= 40 && f.Length >= 2 + (2 * W(f, 0))
        && Enumerable.Range(0, W(f, 0)).Any(i => W(f, 2 + (2 * i)) == 130);

    public static string? Locate(CtrRom rom)
    {
        foreach (var path in rom.Paths.Where(p => p.StartsWith("a/", StringComparison.Ordinal)).Order(StringComparer.Ordinal))
        {
            var head = rom.Peek(path, 4);
            if (head.Length < 4 || Encoding.ASCII.GetString(head, 0, 4) != "CRAG")
            {
                continue;
            }

            Garc g;
            try
            {
                g = new Garc(rom.ReadFile(path));
            }
            catch (ArgumentOutOfRangeException)
            {
                continue;
            }

            if (IsEggMoves(g))
            {
                return path;
            }
        }

        return null;
    }

    public static void Read(N3dsModule.State s, GameData data)
    {
        if (s.Tables.EggMoves is not { } path)
        {
            return;
        }

        var garc = new Garc(s.File(path));
        var moveKeys = s.MoveIndex.ToDictionary(kv => kv.Value, kv => kv.Key);
        for (var i = 0; i < data.Learnsets.Count; i++)
        {
            var l = data.Learnsets[i];
            if (!s.SpeciesIndex.TryGetValue(l.Key, out var index) || index >= garc.Count)
            {
                continue;
            }

            var f = garc[index];
            var count = f.Length >= 2 ? Math.Min(W(f, 0), (f.Length - 2) / 2) : 0;
            var moves = Enumerable.Range(0, count).Select(k => moveKeys.GetValueOrDefault(W(f, 2 + (2 * k)), "MOVE_NONE")).ToList();
            var updated = l with { EggMoves = moves, LockedParts = l.LockedParts.Where(kv => kv.Key != "egg").ToDictionary(kv => kv.Key, kv => kv.Value) };
            data.Learnsets[i] = updated;
            s.Learnsets[l.Key] = updated;
            s.MostEggMoves = Math.Max(s.MostEggMoves, count);
        }
    }

    public static void Write(N3dsModule.State s, GameData data, Dictionary<string, byte[]> changed)
    {
        if (s.Tables.EggMoves is not { } path)
        {
            return;
        }

        var edits = data.Learnsets.Where(l => s.Learnsets.TryGetValue(l.Key, out var before) && !before.EggMoves.SequenceEqual(l.EggMoves) && s.SpeciesIndex.ContainsKey(l.Key)).ToList();
        if (edits.Count == 0)
        {
            return;
        }

        var garc = new Garc(s.File(path));
        foreach (var l in edits)
        {
            var moves = l.EggMoves.Where(s.MoveIndex.ContainsKey).Select(m => s.MoveIndex[m]).ToList();
            var file = new byte[(2 + (2 * moves.Count) + 3) & ~3]; // entries keep 4-byte alignment like the originals
            BinaryPrimitives.WriteUInt16LittleEndian(file, (ushort)moves.Count);
            for (var k = 0; k < moves.Count; k++)
            {
                BinaryPrimitives.WriteUInt16LittleEndian(file.AsSpan(2 + (2 * k)), (ushort)moves[k]);
            }

            garc[s.SpeciesIndex[l.Key]] = file;
        }

        changed[path] = garc.Build();
    }

    public static IEnumerable<ValidationIssue> Check(N3dsModule.State s, GameData data)
    {
        if (s.Tables.EggMoves is null)
        {
            yield break;
        }

        var max = Math.Max(16, s.MostEggMoves);
        foreach (var l in data.Learnsets.Where(l => l.EggMoves.Count > max))
        {
            yield return new ValidationIssue(IssueSeverity.Error, "Learnsets", l.Key, $"These games use at most {max} egg moves per Pokémon.") { TargetKey = l.Key };
        }
    }
}
