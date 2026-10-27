using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Progression;

/// <summary>One wild Pokémon swapped for another in an area (every slot it had, levels kept).</summary>
public sealed record EncounterSwap(string Method, string From, string To, string Why);

/// <summary>A major battle, its bosses' main types and the wild Pokémon that answer them before it (species, area).</summary>
public sealed record BattleReadiness(int Index, string Label, IReadOnlyList<string> Bosses, IReadOnlyList<string> MainTypes, IReadOnlyList<(string Species, string Area)> Counters)
{
    public bool Ready => Counters.Count >= EncounterPlanner.EnoughCounters;
}

/// <summary>What the planner would change in one area.</summary>
public sealed record EncounterPlanRow(IReadOnlyList<string> TableKeys, string Title, string Before, IReadOnlyList<string> Threats, IReadOnlyList<EncounterSwap> Swaps);

/// <summary>
/// Wild Pokémon that fit the story: each area offers Pokémon that do well against the next major battles (the ones whose
/// level cap comes after the area's levels), in the area's habitat (Water types on water, Rock and Ground types when
/// smashing rocks, no pure Water types on land), different from nearby areas, never legendary. The Pokémon that
/// help least against those battles are the ones replaced, up to the share chosen. Levels stay as they were.
/// </summary>
public static class EncounterPlanner
{
    public sealed record Options(double Share = 0.75, int Lookahead = 2);

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

        var bosses = Bosses(data, lab, species);
        var next = NextBattles(data);

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
        var starterFamilies = data.Starters.Concat(Starters.Where(species.ContainsKey)).Select(Family).ToHashSet(StringComparer.Ordinal);
        var forms = new Dictionary<(string, int), string>();
        string FormAt(string root, int level) =>
            forms.TryGetValue((root, level), out var f) ? f : forms[(root, level)] = EvolutionPaths.EvolvedForm(data, root, level, 99);

        // a map's tables (FireRed and LeafGreen versions, for example) are planned once, in playthrough order
        var areas = Areas(data);

        var rows = new List<EncounterPlanRow>();
        var usedNearby = new Dictionary<int, HashSet<string>>(); // families placed per stretch before a major battle, for variety
        foreach (var (map, group, _, position) in areas)
        {
            var table = group.OrderByDescending(t => t.Methods.Sum(m => m.Slots.Count)).First();
            var inArea = group.SelectMany(t => t.Methods).SelectMany(m => m.Slots).Select(s => Family(s.Species)).ToHashSet(StringComparer.Ordinal);
            var swaps = new List<EncounterSwap>();
            var firstNext = -1;
            var firstThreats = new List<string>();
            foreach (var method in table.Methods.Where(m => m.Slots.Count > 0))
            {
                // the next major battle in the playthrough (a Super Rod's stronger Pokémon can only lead to a later one)
                var methodNext = next(map, method.Slots.Max(s => s.MaxLevel), method.Type, 0);
                var threats = new List<string>();
                var weights = Threats(bosses, methodNext, options.Lookahead);
                threats.AddRange(weights.OrderByDescending(w => w.Value).ThenBy(w => w.Key, StringComparer.Ordinal).Select(w => w.Key).Take(4));
                if (threats.Count == 0)
                {
                    continue;
                }

                if (firstNext < 0)
                {
                    (firstNext, firstThreats) = (methodNext, threats);
                }

                if (!usedNearby.TryGetValue(methodNext, out var used))
                {
                    usedNearby[methodNext] = used = [];
                }

                var weighed = threats.Select(t => (Type: t, Weight: weights[t])).ToList();
                double Value(string key) => species.TryGetValue(key, out var s) ? CounterValue(s.Types, weighed) : 0;
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
                            && !(lab.StoryLocks.TryGetValue(r, out var unlock) && methodNext < unlock))
                        .Select(r => (Root: r, Form: FormAt(r, level)))
                        .Where(x => species.TryGetValue(x.Form, out var s) && habitat(s.Types) && s.Stats.Total <= maxTotal)
                        .Select(x => (x.Root, x.Form, Score: Value(x.Form)))
                        .Where(x => x.Score > Math.Max(0, Value(old)) && HitsHard(species[x.Form].Types, threats[0]))
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

    /// <summary>The types most of the coming bosses use (the very next battle counting double), weighed.</summary>
    private static Dictionary<string, double> Threats(List<Dictionary<string, Dictionary<string, double>>> bosses, int next, int lookahead)
    {
        var weights = new Dictionary<string, double>(StringComparer.Ordinal);
        for (var i = next; i < Math.Min(bosses.Count, next + lookahead); i++)
        {
            foreach (var (type, w) in bosses[i].Values.SelectMany(x => x))
            {
                weights[type] = weights.GetValueOrDefault(type) + (w * (i == next ? 2 : 1));
            }
        }

        return weights;
    }

    /// <summary>
    /// The bosses of each milestone: each counts once (a rival's versions merge by name), with the types of their team
    /// (the leaders, Elite Four, Champions and story battles weigh most; a rival's team follows the player's starter).
    /// </summary>
    private static List<Dictionary<string, Dictionary<string, double>>> Bosses(GameData data, LabSettings lab, Dictionary<string, Species> species)
    {
        var caps = data.Rules?.LevelCaps ?? [];
        var bosses = new List<Dictionary<string, Dictionary<string, double>>>();
        for (var i = 0; i < caps.Count; i++)
        {
            bosses.Add(new Dictionary<string, Dictionary<string, double>>(StringComparer.Ordinal));
        }

        // rematches (Emerald's TRAINER_ROXANNE_2…5) come after the League: they don't shape the way there
        foreach (var t in data.Trainers.Where(t => !t.Key.Contains('@', StringComparison.Ordinal) && t.Party.Count > 0 && !t.NeverFought && !Lab.IsRematch(data, t)
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

        return bosses;
    }

    /// <summary>
    /// The wild areas, one per map (FireRed and LeafGreen versions of a table together), in playthrough order when the
    /// game has one (otherwise by level). Battle facilities aren't areas, nor Emerald's copies of FireRed's areas.
    /// </summary>
    public static IReadOnlyList<(string Map, IReadOnlyList<EncounterTable> Tables, int Level, double Position)> Areas(GameData data)
    {
        var hoenn = data.Encounters.Any(t => t.Map == "MAP_ROUTE101");
        var order = StoryOrder.For(data);
        return data.Encounters.Where(t => t.LockedReason is null && t.Map.Length > 0 && t.Methods.Any(m => m.Slots.Count > 0)
                && !(hoenn && (t.Key.EndsWith("_FireRed", StringComparison.Ordinal) || t.Key.EndsWith("_LeafGreen", StringComparison.Ordinal))))
            .GroupBy(t => t.Map)
            .Select(g => (Map: g.Key, Tables: (IReadOnlyList<EncounterTable>)g.ToList(), Level: g.SelectMany(t => t.Methods).SelectMany(m => m.Slots).Max(s => s.MaxLevel)))
            .Select(a => (a.Map, a.Tables, a.Level, Position: order is null ? a.Level : StoryOrder.AreaPosition(order, a.Map)!.Value))
            .OrderBy(a => a.Position).ThenBy(a => a.Level).ThenBy(a => a.Map, StringComparer.Ordinal).ToList();
    }

    /// <summary>
    /// The next major battle (a level-cap index) after meeting a wild Pokémon of a level on a map: from the playthrough
    /// order when the game has one (never earlier than its level allows), otherwise from the level alone.
    /// </summary>
    public static Func<string, int, string, int, int> NextBattles(GameData data)
    {
        var caps = data.Rules?.LevelCaps ?? [];
        var order = StoryOrder.For(data);
        var positions = StoryOrder.CapPositions(data);
        int ByLevel(int level) => Enumerable.Range(0, caps.Count).FirstOrDefault(i => caps[i].Level >= level, caps.Count - 1);

        // the area's place, or later when the way of meeting it (a rod, Surf, Rock Smash) opens up later
        return (map, level, method, slot) => order is null || positions is null
            ? ByLevel(level)
            : Math.Max(StoryOrder.NextBattle(positions, Math.Max(StoryOrder.AreaPosition(order, map)!.Value, StoryOrder.Gate(order, method, slot))), ByLevel(level));
    }

    /// <summary>The fewest different families that should answer a major battle's main type before the player gets there.</summary>
    public const int EnoughCounters = 2;

    /// <summary>
    /// The playthrough battle by battle: for each major battle, its bosses' main types and the wild Pokémon a player can
    /// catch on the way there (any earlier area) that hit one of those types hard, one per family, in the order they're met.
    /// </summary>
    public static IReadOnlyList<BattleReadiness> Check(GameData data, LabSettings lab)
    {
        var caps = data.Rules?.LevelCaps ?? [];
        if (caps.Count == 0)
        {
            return [];
        }

        var species = data.Species.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var bosses = Bosses(data, lab, species);
        var next = NextBattles(data);
        string Family(string key) => species.TryGetValue(TeamBuilder.FamilyRoot(data, key), out var r) ? r.Name.ToUpperInvariant() : key;

        var met = Met(data);

        var result = new List<BattleReadiness>();
        for (var i = 0; i < caps.Count; i++)
        {
            var weights = Threats(bosses, i, 1);
            if (weights.Count == 0)
            {
                continue;
            }

            var top = weights.Values.Max();
            var main = weights.Where(w => w.Value >= top * 0.6).OrderByDescending(w => w.Value).ThenBy(w => w.Key, StringComparer.Ordinal).Select(w => w.Key).ToList();
            var counters = met.OrderBy(m => m.From).Where(m => m.From <= i && species.TryGetValue(m.Species, out var s) && main.Any(t => s.Types.Any(a => TypeChart.Multiplier(a, [t]) > 1)))
                .DistinctBy(m => Family(m.Species)).Select(m => (m.Species, m.Area)).ToList();
            var names = bosses[i].Keys.Select(GameCatalog.Prettify).ToList();
            result.Add(new BattleReadiness(i, caps[i].Label, names, main, counters));
        }

        return result;
    }

    /// <summary>
    /// Every wild Pokémon with the first major battle it can be caught before (an index into the level caps), in
    /// playthrough order; a Pokémon counts from its earliest slot (the Old Rod's before the Super Rod's).
    /// </summary>
    public static List<(string Species, string Area, int From)> Met(GameData data)
    {
        var next = NextBattles(data);
        var met = new List<(string Species, string Area, int From)>();
        foreach (var (map, tables, _, _) in Areas(data))
        {
            foreach (var method in tables.SelectMany(t => t.Methods).Where(m => m.Slots.Count > 0))
            {
                for (var slot = 0; slot < method.Slots.Count; slot++)
                {
                    met.Add((method.Slots[slot].Species, Title(tables[0]), next(map, method.Slots[slot].MinLevel, method.Type, slot)));
                }
            }
        }

        return met;
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
    private static double CounterValue(IReadOnlyList<string> types, IReadOnlyList<(string Type, double Weight)> threats)
    {
        var score = 0.0;
        if (types.Count == 0)
        {
            return score;
        }

        // each boss type counts as much as the bosses use it
        foreach (var (t, w) in threats)
        {
            score += w * (types.Max(a => TypeChart.Multiplier(a, [t])) - 1);
            score += w * (1 - TypeChart.Multiplier(t, types));
        }

        return score;
    }

    /// <summary>A real answer: one of its types hits the coming bosses' main type super-effectively.</summary>
    private static bool HitsHard(IReadOnlyList<string> types, string mainThreat) =>
        types.Any(a => TypeChart.Multiplier(a, [mainThreat]) > 1);

    /// <summary>
    /// Where a Pokémon can live: Water types on water, Rock and Ground for rock smashing; on land anything but a pure Water
    /// type (a Route 1 of only Normal and Flying types would otherwise never offer an answer to a Rock gym).
    /// </summary>
    private static Func<IReadOnlyList<string>, bool> Habitat(string method, HashSet<string> areaTypes) => method switch
    {
        _ when method.Contains("water", StringComparison.OrdinalIgnoreCase) || method.Contains("fish", StringComparison.OrdinalIgnoreCase)
            => t => t.Contains("TYPE_WATER"),
        _ when method.Contains("rock", StringComparison.OrdinalIgnoreCase) => t => t.Contains("TYPE_ROCK") || t.Contains("TYPE_GROUND"),
        _ => t => !(t.Count > 0 && t.All(x => x == "TYPE_WATER")),
    };

    private static readonly string[] Regional = ["_ALOLA", "_GALAR", "_HISUI", "_PALDEA"];

    /// <summary>Every region's starters: they stay special (gifts), never common wild Pokémon.</summary>
    private static readonly string[] Starters =
    [
        "SPECIES_BULBASAUR", "SPECIES_CHARMANDER", "SPECIES_SQUIRTLE", "SPECIES_CHIKORITA", "SPECIES_CYNDAQUIL", "SPECIES_TOTODILE",
        "SPECIES_TREECKO", "SPECIES_TORCHIC", "SPECIES_MUDKIP", "SPECIES_TURTWIG", "SPECIES_CHIMCHAR", "SPECIES_PIPLUP",
        "SPECIES_SNIVY", "SPECIES_TEPIG", "SPECIES_OSHAWOTT", "SPECIES_CHESPIN", "SPECIES_FENNEKIN", "SPECIES_FROAKIE",
        "SPECIES_ROWLET", "SPECIES_LITTEN", "SPECIES_POPPLIO", "SPECIES_GROOKEY", "SPECIES_SCORBUNNY", "SPECIES_SOBBLE",
        "SPECIES_SPRIGATITO", "SPECIES_FUECOCO", "SPECIES_QUAXLY",
    ];

    private static string Short(string type) => GameCatalog.Prettify(type.Replace("TYPE_", "", StringComparison.Ordinal));

    private static string Title(EncounterTable t) => t.Map.Length > 0 ? StoryOrder.AreaName(t.Map) : t.Key;

    /// <summary>Night-themed families besides every Ghost and Dark type: owls, bats, moths, glowing and moon Pokémon.</summary>
    public static readonly IReadOnlyList<string> NightFamilies =
    [
        "SPECIES_HOOTHOOT", "SPECIES_ZUBAT", "SPECIES_NOIBAT", "SPECIES_WOOBAT", "SPECIES_SPINARAK", "SPECIES_VENONAT", "SPECIES_ODDISH",
        "SPECIES_KRICKETOT", "SPECIES_VOLBEAT", "SPECIES_ILLUMISE", "SPECIES_MORELULL", "SPECIES_CLEFFA", "SPECIES_CLEFAIRY", "SPECIES_MEOWTH",
        "SPECIES_RATTATA", "SPECIES_DUSKULL", "SPECIES_LITWICK", "SPECIES_SNEASEL",
    ];

    /// <summary>The grass slots a night table changes: every third one from the second (about a third of the encounters).</summary>
    private static bool NightSlot(int i) => i % 3 == 1;

    /// <summary>
    /// A night version of each area's grass (and cave) table: about a third of the encounters become night Pokémon (Ghost and
    /// Dark types and <see cref="NightFamilies"/>) that fit the place by the same rules as the plan (as strong as a wild
    /// Pokémon at that level can be, not a legendary or a starter, not before its story lock), one family each per area.
    /// Keyed like the game's own time tables ("gRoute1_Night"); the other ways of meeting Pokémon stay as by day.
    /// </summary>
    public static IReadOnlyList<EncounterTable> NightTables(GameData data, LabSettings lab)
    {
        var species = data.Species.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var next = data.Rules?.LevelCaps.Count > 0 ? NextBattles(data) : null;
        var officialOnly = lab.OfficialOnly && data.Species.Any(s => s.NationalDex is not null);
        var starters = data.Starters.Concat(Starters).Where(species.ContainsKey).Select(k => TeamBuilder.FamilyRoot(data, k)).ToHashSet(StringComparer.Ordinal);
        bool Night(Species s) => s.Types.Any(t => t is "TYPE_GHOST" or "TYPE_DARK") || NightFamilies.Contains(TeamBuilder.FamilyRoot(data, s.Key));
        var roots = data.Species.Where(s => s.IsEnabled && !s.LockedFields.ContainsKey("*") && s.Stats.Total > 0 && (s.Tags & Special) == 0
                && TeamBuilder.IsRegularForm(s) && (!officialOnly || TeamBuilder.IsOfficial(s)) && !Regional.Any(r => s.Key.Contains(r, StringComparison.Ordinal)))
            .Select(s => TeamBuilder.FamilyRoot(data, s.Key)).Distinct(StringComparer.Ordinal)
            .Where(r => species.TryGetValue(r, out var s) && (s.Tags & Special) == 0 && !starters.Contains(r)
                && data.Species.Any(x => x.IsEnabled && TeamBuilder.FamilyRoot(data, x.Key) == r && Night(x)))
            .OrderBy(r => r, StringComparer.Ordinal).ToList();

        var result = new List<EncounterTable>();
        foreach (var table in data.Encounters.Where(t => t.Map.Length > 0 && !t.Key.EndsWith("_Night", StringComparison.Ordinal)))
        {
            if (table.Methods.FirstOrDefault(m => m.Type == "land_mons" && m.Slots.Count > 0) is not { } land)
            {
                continue;
            }

            var inArea = land.Slots.Select(s => TeamBuilder.FamilyRoot(data, s.Species)).ToHashSet(StringComparer.Ordinal);
            var picked = new Dictionary<string, string>(StringComparer.Ordinal); // day species → night one, so a species' slots change together
            var slots = new List<EncounterSlot>();
            for (var i = 0; i < land.Slots.Count; i++)
            {
                var day = land.Slots[i];
                if (!NightSlot(i))
                {
                    slots.Add(day);
                    continue;
                }

                if (!picked.TryGetValue(day.Species, out var night))
                {
                    var milestone = next?.Invoke(table.Map, day.MinLevel, "land_mons", i) ?? int.MaxValue;
                    var maxTotal = Math.Max(species.TryGetValue(day.Species, out var o) ? o.Stats.Total : 0, 260 + (7 * day.MaxLevel));
                    night = roots
                        .Where(r => !inArea.Contains(r) && !(lab.StoryLocks.TryGetValue(r, out var unlock) && milestone < unlock))
                        .Select(r => EvolutionPaths.EvolvedForm(data, r, day.MaxLevel, 99))
                        .Where(k => species.TryGetValue(k, out var s) && s.Stats.Total <= maxTotal && Night(s))
                        .OrderBy(k => StableHash($"{table.Key}/{day.Species}/{k}")).FirstOrDefault() ?? day.Species;
                    picked[day.Species] = night;
                    inArea.Add(TeamBuilder.FamilyRoot(data, night));
                }

                slots.Add(day with { Species = night });
            }

            if (slots.SequenceEqual(land.Slots))
            {
                continue;
            }

            result.Add(new EncounterTable
            {
                Key = table.Key + "_Night",
                Map = table.Map,
                Group = table.Group,
                Methods = [land with { Slots = slots }],
            });
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
