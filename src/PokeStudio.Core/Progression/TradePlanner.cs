using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Progression;

/// <summary>One in-game trade: the trader's place ("Route 2 House"), the Pokémon they ask for and the one they give.</summary>
public sealed record InGameTrade(string Id, string Place, string Requested, string Offered);

/// <summary>
/// In-game trades that ask for a Pokémon the player can actually have: when the requested one's family isn't in the wild
/// before the trader, it becomes one that is, sharing a type with the original when it can (an evolved request stays
/// evolved, as far as the level cap allows). The Pokémon the trader gives stays.
/// </summary>
public static class TradePlanner
{
    /// <summary>Trade id → the Pokémon to ask for instead; trades already fine are left out.</summary>
    public static IReadOnlyDictionary<string, string> Requests(GameData data, IEnumerable<InGameTrade> trades)
    {
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        var caps = data.Rules?.LevelCaps ?? [];
        var met = EncounterPlanner.Met(data);
        if (caps.Count == 0 || met.Count == 0)
        {
            return result;
        }

        var species = data.Species.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var roots = met.Select(m => (Root: TeamBuilder.FamilyRoot(data, m.Species), m.From))
            .GroupBy(x => x.Root).Select(g => (Root: g.Key, From: g.Min(x => x.From))).ToList();
        foreach (var trade in trades)
        {
            if (!species.TryGetValue(trade.Requested, out var original))
            {
                continue;
            }

            var milestone = StoryOrder.MilestoneOfPlace(data, trade.Place) ?? caps.Count - 1;
            var root = TeamBuilder.FamilyRoot(data, trade.Requested);
            var offeredRoot = species.ContainsKey(trade.Offered) ? TeamBuilder.FamilyRoot(data, trade.Offered) : trade.Offered;
            var available = roots.Where(r => r.From <= milestone && r.Root != offeredRoot).Select(r => r.Root).ToList();
            if (available.Contains(root) || available.Count == 0)
            {
                continue;
            }

            // an evolved request (Poliwhirl, Raichu) stays evolved, as far as the level cap allows
            var evolved = root != trade.Requested;
            var level = caps[Math.Min(milestone, caps.Count - 1)].Level;
            var options = available.Select(r => evolved ? EvolutionPaths.EvolvedForm(data, r, level, 36) : r)
                .Where(species.ContainsKey).Distinct(StringComparer.Ordinal).ToList();
            var sameType = options.Where(k => species[k].Types.Any(original.Types.Contains)).ToList();
            var pool = sameType.Count > 0 ? sameType : options;
            result[trade.Id] = pool.OrderBy(k => StableHash($"{trade.Id}/{k}")).First();
        }

        return result;
    }

    private static int StableHash(string text)
    {
        unchecked
        {
            var h = 17;
            foreach (var c in text)
            {
                h = (h * 31) + c;
            }

            return h & 0x7FFFFFFF;
        }
    }
}
