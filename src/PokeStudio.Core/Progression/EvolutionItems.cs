using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Progression;

/// <summary>An item some evolution needs, the Pokémon that need it, and whether the player can get it.</summary>
public sealed record EvolutionItem(string Item, string Name, IReadOnlyList<string> Species, bool Obtainable);

/// <summary>
/// Makes sure every item an evolution needs (stones, Sweets, the Linking Cord, held trade items…) can be obtained: found
/// in the world, given by someone, or sold in a shop. Missing ones are added to the shop that already sells evolution
/// stones (or the biggest shop).
/// </summary>
public static class EvolutionItems
{
    /// <summary>Every item an evolution of an enabled Pokémon needs, with whether it's obtainable.</summary>
    /// <param name="inWorld">Items the game places or gives (item balls, hidden items, gifts, shops), from the game module.</param>
    public static IReadOnlyList<EvolutionItem> Check(GameData data, IReadOnlyCollection<string> inWorld)
    {
        var enabled = data.Species.Where(s => s.IsEnabled).Select(s => s.Key).ToHashSet(StringComparer.Ordinal);
        var sold = data.Marts.SelectMany(m => m.Items.Concat(m.BadgeTiers.Values.SelectMany(v => v))).ToHashSet(StringComparer.Ordinal);
        var known = data.Items.ToDictionary(i => i.Key, StringComparer.Ordinal);
        var needed = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        foreach (var set in data.Evolutions.Where(s => enabled.Contains(s.FromSpecies)))
        {
            foreach (var e in set.Evolutions)
            {
                var items = e.Conditions.Where(c => c.Kind == "IF_HOLD_ITEM").Select(c => c.Value).ToList();
                if (e.Method == "EVO_ITEM")
                {
                    items.Add(e.Parameter);
                }

                foreach (var item in items.Where(known.ContainsKey))
                {
                    if (!needed.TryGetValue(item, out var list))
                    {
                        needed[item] = list = [];
                    }

                    if (!list.Contains(set.FromSpecies))
                    {
                        list.Add(set.FromSpecies);
                    }
                }
            }
        }

        return needed.OrderBy(n => known[n.Key].Name, StringComparer.Ordinal)
            .Select(n => new EvolutionItem(n.Key, known[n.Key].Name, n.Value, inWorld.Contains(n.Key) || sold.Contains(n.Key)))
            .ToList();
    }

    /// <summary>The shop missing items go to: the one already selling evolution stones, else the one with the most stock.</summary>
    public static Mart? ShopFor(GameData data) =>
        data.Marts.Where(m => m.LockedReason is null)
            .OrderByDescending(m => m.Items.Count(i => i.EndsWith("_STONE", StringComparison.Ordinal)))
            .ThenByDescending(m => m.Items.Count)
            .FirstOrDefault();

    /// <summary>Adds the items to <paramref name="shop"/>'s stock as one undo step (null when there's nothing to add).</summary>
    public static IChange? AddToShop(GameData data, Mart shop, IEnumerable<string> items)
    {
        var add = items.Where(i => !shop.Items.Contains(i)).Distinct(StringComparer.Ordinal).ToList();
        var index = data.Marts.FindIndex(m => m.Key == shop.Key);
        if (add.Count == 0 || index < 0)
        {
            return null;
        }

        var before = data.Marts[index];
        return new ValueChange<Mart>("Items & shops", $"{before.Place}: {add.Count} evolution items", v => data.Marts[data.Marts.FindIndex(m => m.Key == before.Key)] = v,
            before, before with { Items = [.. before.Items, .. add] });
    }
}
