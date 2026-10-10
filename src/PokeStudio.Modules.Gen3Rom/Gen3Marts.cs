using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Modules.Gen3Rom;

/// <summary>Shops in a Gen 3 ROM: the item lists map scripts open with "pokemart". Longer lists move to free space.</summary>
internal static class Gen3Marts
{
    public sealed record Shop(int Offset, int Count, List<int> References);

    public static void Load(Gen3RomModule.State state, GameData data, Dictionary<int, (List<int> Refs, string Place)> found)
    {
        state.Shops.Clear();
        var keys = state.ItemIndex.ToDictionary(kv => kv.Value, kv => kv.Key);
        var perPlace = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (offset, (refs, place)) in found.OrderBy(f => f.Value.Refs.Min()))
        {
            refs.Sort();
            var count = Gen3Dialogue.MartLength(state.Rom, offset, state.Tables.ItemCount);
            var items = Enumerable.Range(0, count).Select(i => keys.GetValueOrDefault(state.Rom.U16(offset + (2 * i)), "ITEM_NONE")).ToList();
            perPlace[place] = perPlace.GetValueOrDefault(place) + 1;
            var mart = new Mart
            {
                Key = $"MART_{refs[0]:X6}",
                Place = perPlace[place] == 1 ? $"{place} shop" : $"{place} shop {perPlace[place]}",
                Items = items,
                TiersLockedReason = "Stock that grows with badges needs a project (decomp).",
            };
            data.Marts.Add(mart);
            state.Marts[mart.Key] = mart;
            state.Shops[mart.Key] = new Shop(offset, count, refs);
        }
    }

    public static void Save(Gen3RomModule.State state, GameData data)
    {
        var rom = state.Rom;
        foreach (var mart in data.Marts)
        {
            if (!state.Marts.TryGetValue(mart.Key, out var before) || ReferenceEquals(before, mart) || before.Items.SequenceEqual(mart.Items) || !state.Shops.TryGetValue(mart.Key, out var shop))
            {
                continue;
            }

            var bytes = new byte[(2 * mart.Items.Count) + 2];
            for (var i = 0; i < mart.Items.Count; i++)
            {
                System.Buffers.Binary.BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(2 * i), (ushort)state.ItemIndex[mart.Items[i]]);
            }

            if (mart.Items.Count <= shop.Count)
            {
                bytes.CopyTo(rom.Bytes, shop.Offset);
                continue;
            }

            var at = rom.FindFree(bytes.Length);
            if (at < 0)
            {
                throw new InvalidOperationException("The ROM has no free space left for the bigger shop.");
            }

            bytes.CopyTo(rom.Bytes, at);
            foreach (var r in shop.References)
            {
                rom.WritePointer(r, at);
            }
        }
    }
}
