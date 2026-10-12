using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Progression;

/// <summary>One wild Pokémon swapped for another in an area (every slot it had, levels kept).</summary>
public sealed record EncounterSwap(string Method, string From, string To, string Why);

/// <summary>What the planner would change in one area.</summary>
public sealed record EncounterPlanRow(IReadOnlyList<string> TableKeys, string Title, string Before, IReadOnlyList<string> Threats, IReadOnlyList<EncounterSwap> Swaps);

/// <summary>
/// Wild Pokémon that fit the story: each area offers Pokémon that do well against the next major battles (the ones whose
/// level cap comes after the area's levels), in the area's habitat (Water types on water, Rock and Ground types when
/// smashing rocks, grass keeps a type of its own Pokémon), different from nearby areas, never legendary. The Pokémon that
/// help least against those battles are the ones replaced, up to the share chosen. Levels stay as they were.
/// </summary>
public static class EncounterPlanner
{
    public sealed record Options(double Share = 0.5, int Lookahead = 2);

    private static readonly SpeciesTags Special = SpeciesTags.RestrictedLegendary | SpeciesTags.SubLegendary | SpeciesTags.Mythical
        | SpeciesTags.UltraBeast | SpeciesTags.Paradox | SpeciesTags.Mega;

    public static IReadOnlyList<EncounterPlanRow> Plan(GameData data, LabSettings lab, Options options)
    {
        var caps = data.Rules?.LevelCaps ?? [];
        if (caps.Count == 0)
        {
            return [];
        }

        var species = data.Species.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var rootOf = new Dictionary<string, string>(StringComparer.Ordinal);
        string Root(string key) => rootOf.TryGetValue(key, out var r) ? r : rootOf[key] = TeamBuilder.FamilyRoot(data, key);

        // a family by its first member's name, so a species' forms (Deerling's seasons, Pumpkaboo's sizes…) count once
        string Family(string key) => species.TryGetValue(Root(key), out var r) ? r.Name.ToUpperInvariant() : Root(key);

        // the bosses of each milestone: each counts once (a rival's versions merge by name), with the types of their team
        // (the leaders, Elite Four, Champions and story battles weigh most; a rival's team follows the player's starter)
        var bosses = new List<Dictionary<string, Dictionary<string, double>>>();
        for (var i = 0; i < caps.Count; i++)
        {
            bosses.Add(new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal));
        }

        foreach (var t in data.Trainers.Where(t => !t.Key.Contains('@', StringComparison.Ordinal) && t.Party.Count > 0 && !t.NeverFought
                     && (Lab.GroupOf(data, lab, t) != TrainerGroup.Regular || t.Key.StartsWith(Story.StoryBattles.TrainerPrefix, StringComparison.Ordinal))))
        {
            var m = Lab.MilestoneOf(data, t);
            if (m < 0)
            {
                continue;
            }

            if (!bosses[m].TryGetValue(t.Name, out var types))
            {
                bosses[m][t.Name] = types = new Dictionary<string, double>(StringComparer.Ordinal);
            }

            var group = Lab.GroupOf(data, lab, t);
            var weight = group == TrainerGroup.Rival ? 1.0 : 3.0;
            foreach (var type in t.Party.SelectMany(p => species.TryGetValue(p.Species, out var s) ? s.Types.Distinct() : []))
            {
                // a share of the team: a mono-type leader's type weighs its full weight
                types[type] = Math.Max(types.GetValueOrDefault(type), weight * t.Party.Count(p => species.TryGetValue(p.Species, out var s) && s.Types.Contains(type)) / t.Party.Count);
            }
        }

        // candidates: ordinary official Pokémon in their main or regional form, one family each (the stage comes from the level)
        var officialOnly = lab.OfficialOnly && data.Species.Any(s => s.NationalDex is not null);
        var mainForm = data.Species.Where(s => s.NationalDex is not null).GroupBy(s => s.NationalDex)
            .Select(g => g.OrderBy(s => s.Key.Length).First().Key).ToHashSet(StringComparer.Ordinal);
        bool UsualForm(Species s) => s.NationalDex is null || mainForm.Contains(s.Key) || Regional.Any(r => s.Key.Contains(r, StringComparison.Ordinal));
        var roots = data.Species.Where(s => s.IsEnabled && !s.LockedFields.ContainsKey("*") && s.Stats.Total > 0 && (s.Tags & Special) == 0
                && TeamBuilder.IsRegularForm(s) && UsualForm(s) && (!officialOnly || TeamBuilder.IsOfficial(s)))
            .Select(s => Root(s.Key)).Distinct(StringComparer.Ordinal)
            .Where(r => species.TryGetValue(r, out var s) && (s.Tags & Special) == 0 && UsualForm(s))
            .GroupBy(Family).Select(g => g.OrderBy(r => r.Length).ThenBy(r => r, StringComparer.Ordinal).First())
            .ToList();
        var starterFamilies = data.Starters.Select(Family).ToHashSet(StringComparer.Ordinal);
        var forms = new Dictionary<(string, int), string>();
        string FormAt(string root, int level) =>
            forms.TryGetValue((root, level), out var f) ? f : forms[(root, level)] = EvolutionPaths.EvolvedForm(data, root, level, 99);

        // a map's tables (FireRed and LeafGreen versions, for example) are planned once; battle facilities aren't areas
        // (Emerald's project also has FireRed/LeafGreen's areas for its FireRed build: not part of an Emerald game)
        var hoenn = data.Encounters.Any(t => t.Map == "MAP_ROUTE101");
        var areas = data.Encounters.Where(t => t.LockedReason is null && t.Map.Length > 0 && t.Methods.Any(m => m.Slots.Count > 0)
                && !(hoenn && (t.Key.EndsWith("_FireRed", StringComparison.Ordinal) || t.Key.EndsWith("_LeafGreen", StringComparison.Ordinal))))
            .GroupBy(t => t.Map)
            .Select(g => (Map: g.Key, Tables: g.ToList(), Level: g.SelectMany(t => t.Methods).SelectMany(m => m.Slots).Max(s => s.MaxLevel)))
            .OrderBy(a => a.Level).ThenBy(a => a.Map, StringComparer.Ordinal).ToList();

        var rows = new List<EncounterPlanRow>();
        var usedNearby = new Dictionary<int, HashSet<string>>(); // families placed per milestone stretch, for variety
        foreach (var (map, group, areaLevel) in areas)
        {
            var table = group.OrderByDescending(t => t.Methods.Sum(m => m.Slots.Count)).First();
            var inArea = group.SelectMany(t => t.Methods).SelectMany(m => m.Slots).Select(s => Family(s.Species)).ToHashSet(StringComparer.Ordinal);
            var swaps = new List<EncounterSwap>();
            var firstNext = -1;
            var firstThreats = new List<string>();
            foreach (var method in table.Methods.Where(m => m.Slots.Count > 0))
            {
                // each way of meeting Pokémon has its own levels (a Super Rod finds much stronger ones than the grass)
                var methodLevel = method.Slots.Max(s => s.MaxLevel);
                var next = Enumerable.Range(0, caps.Count).FirstOrDefault(i => caps[i].Level >= methodLevel, caps.Count - 1);

                // the types most of the coming bosses use, the very next battle counting double
                var weights = new Dictionary<string, double>(StringComparer.Ordinal);
                for (var i = next; i < Math.Min(caps.Count, next + options.Lookahead); i++)
                {
                    foreach (var (type, w) in bosses[i].Values.SelectMany(x => x))
                    {
                        weights[type] = weights.GetValueOrDefault(type) + (w * (i == next ? 2 : 1));
                    }
                }

                var threats = weights.OrderByDescending(w => w.Value).ThenBy(w => w.Key, StringComparer.Ordinal).Select(w => w.Key).Take(4).ToList();
                if (threats.Count == 0)
                {
                    continue;
                }

                if (firstNext < 0)
                {
                    (firstNext, firstThreats) = (next, threats);
                }

                if (!usedNearby.TryGetValue(next, out var used))
                {
                    usedNearby[next] = used = [];
                }

                double Value(string key) => species.TryGetValue(key, out var s) ? CounterValue(s.Types, threats) : 0;
                var distinct = method.Slots.Select(s => s.Species).Distinct(StringComparer.Ordinal).ToList();
                var count = (int)Math.Round(distinct.Count * options.Share, MidpointRounding.AwayFromZero);
                var habitat = Habitat(method.Type, distinct.SelectMany(k => species.TryGetValue(k, out var s) ? s.Types : []).ToHashSet(StringComparer.Ordinal));
                foreach (var old in distinct.OrderBy(Value).Take(count))
                {
                    var level = method.Slots.Where(s => s.Species == old).Max(s => s.MaxLevel);

                    // about as strong as a wild Pokémon at this level can be (no Skarmory on Route 1)
                    var maxTotal = Math.Max(species.TryGetValue(old, out var o) ? o.Stats.Total : 0, 260 + (7 * level));
                    var pick = roots
                        .Where(r => !inArea.Contains(Family(r)) && !used.Contains(Family(r)) && !starterFamilies.Contains(Family(r))
                            && !(lab.StoryLocks.TryGetValue(r, out var unlock) && next < unlock))
                        .Select(r => (Root: r, Form: FormAt(r, level)))
                        .Where(x => species.TryGetValue(x.Form, out var s) && habitat(s.Types) && s.Stats.Total <= maxTotal)
                        .Select(x => (x.Root, x.Form, Score: Value(x.Form)))
                        .Where(x => x.Score > Value(old))
                        .OrderByDescending(x => x.Score).ThenByDescending(x => species[x.Form].Stats.Total).ThenBy(x => x.Form, StringComparer.Ordinal)
                        .FirstOrDefault();
                    if (pick.Form is null)
                    {
                        continue;
                    }

                    inArea.Add(Family(pick.Root));
                    used.Add(Family(pick.Root));
                    var good = threats.Where(t => species[pick.Form].Types.Any(a => TypeChart.Multiplier(a, [t]) > 1)).ToList();
                    swaps.Add(new EncounterSwap(method.Type, old, pick.Form,
                        good.Count > 0 ? $"hits {string.Join(", ", good.Select(Short))} hard" : $"resists {string.Join(", ", threats.Where(t => TypeChart.Multiplier(t, species[pick.Form].Types) < 1).Select(Short))}"));
                }
            }

            if (swaps.Count > 0)
            {
                rows.Add(new EncounterPlanRow(group.Select(t => t.Key).ToList(), Title(table), caps[firstNext].Label, firstThreats, swaps));
            }
        }

        return rows;
    }

    /// <summary>One undo step for the chosen areas.</summary>
    public static IChange? ToChange(GameData data, IEnumerable<EncounterPlanRow> rows)
    {
        var parts = new List<IChange>();
        foreach (var row in rows)
        {
            foreach (var key in row.TableKeys)
            {
                var i = data.Encounters.FindIndex(t => t.Key == key);
                if (i < 0)
                {
                    continue;
                }

                var before = data.Encounters[i];
                var after = before with
                {
                    Methods = before.Methods.Select(m => m with
                    {
                        Slots = m.Slots.Select(s => row.Swaps.FirstOrDefault(x => x.Method == m.Type && x.From == s.Species) is { } swap ? s with { Species = swap.To } : s).ToList(),
                    }).ToList(),
                };
                parts.Add(new ValueChange<EncounterTable>("Wild encounters", row.Title, v => data.Encounters[data.Encounters.FindIndex(t => t.Key == key)] = v, before, after));
            }
        }

        return parts.Count == 0 ? null : new CompositeChange("Wild encounters", $"Wild Pokémon planned for the story in {rows.Count()} areas", parts);
    }

    /// <summary>How well these types do against the bosses' types: hitting them hard and taking little from them.</summary>
    private static double CounterValue(IReadOnlyList<string> types, IReadOnlyList<string> threats)
    {
        var score = 0.0;
        if (types.Count == 0)
        {
            return score;
        }

        foreach (var t in threats)
        {
            score += types.Max(a => TypeChart.Multiplier(a, [t])) - 1;
            score += 1 - TypeChart.Multiplier(t, types);
        }

        return score;
    }

    /// <summary>Where a Pokémon can live: Water types on water, Rock and Ground for rock smashing, otherwise the area's own types.</summary>
    private static Func<IReadOnlyList<string>, bool> Habitat(string method, HashSet<string> areaTypes) => method switch
    {
        _ when method.Contains("water", StringComparison.OrdinalIgnoreCase) || method.Contains("fish", StringComparison.OrdinalIgnoreCase)
            => t => t.Contains("TYPE_WATER"),
        _ when method.Contains("rock", StringComparison.OrdinalIgnoreCase) => t => t.Contains("TYPE_ROCK") || t.Contains("TYPE_GROUND"),
        _ => t => t.Any(areaTypes.Contains),
    };

    private static readonly string[] Regional = ["_ALOLA", "_GALAR", "_HISUI", "_PALDEA"];

    private static string Short(string type) => GameCatalog.Prettify(type.Replace("TYPE_", "", StringComparison.Ordinal));

    private static string Title(EncounterTable t) =>
        t.Map.Length > 0 ? GameCatalog.Prettify(t.Map.Replace("MAP_", "", StringComparison.Ordinal)) : t.Key;
}
