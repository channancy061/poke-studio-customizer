using PokeStudio.Core.Changes;
using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Randomizer;

public enum WildRandomizing
{
    Off,
    Random,

    /// <summary>Each Pokémon is replaced by one with a similar base stat total.</summary>
    SimilarStrength,
}

public enum TrainerRandomizing
{
    Off,
    SimilarStrength,

    /// <summary>Similar strength, and sharing a type with the Pokémon it replaces (gym themes stay).</summary>
    KeepTypes,
}

public sealed record RandomizerOptions
{
    public string Seed { get; init; } = "";
    public WildRandomizing Wild { get; init; } = WildRandomizing.SimilarStrength;

    /// <summary>In each area, a Pokémon is always replaced by the same one (an area keeps its variety).</summary>
    public bool WildSameInArea { get; init; } = true;

    public bool WildLegendaries { get; init; }
    public TrainerRandomizing Trainers { get; init; } = TrainerRandomizing.Off;
    public bool TrainerLegendaries { get; init; }
    public bool Abilities { get; init; }
    public bool Types { get; init; }
    public bool LevelUpMoves { get; init; }
    public bool TmCompatibility { get; init; }

    /// <summary>Only the official Pokémon of the main games (no novelty or event forms).</summary>
    public bool OfficialOnly { get; init; } = true;
}

/// <summary>What a randomizing run changed, in plain numbers.</summary>
public sealed record RandomizerSummary(int WildSlots, int TrainerPokemon, int Abilities, int Types, int Learnsets, int TmLists)
{
    public int Total => WildSlots + TrainerPokemon + Abilities + Types + Learnsets + TmLists;

    public override string ToString() =>
        string.Join(", ", new[] { (WildSlots, "wild slots"), (TrainerPokemon, "trainer Pokémon"), (Abilities, "Pokémon's abilities"), (Types, "Pokémon's types"),
            (Learnsets, "level-up lists"), (TmLists, "TM lists") }.Where(x => x.Item1 > 0).Select(x => $"{x.Item1} {x.Item2}"));
}

/// <summary>
/// Shuffles the hack for a fresh run: wild Pokémon, trainers' teams, abilities, types and moves. The same seed and options
/// always give the same result, and the whole run is one undo step.
/// </summary>
public static class Randomizer
{
    // abilities that break the game when handed out at random
    private static readonly string[] BannedAbilities = ["ABILITY_NONE", "ABILITY_WONDER_GUARD", "ABILITY_TRUANT", "ABILITY_SLOW_START", "ABILITY_DEFEATIST",
        "ABILITY_ZEN_MODE", "ABILITY_STANCE_CHANGE", "ABILITY_SCHOOLING", "ABILITY_DISGUISE", "ABILITY_BATTLE_BOND", "ABILITY_POWER_CONSTRUCT", "ABILITY_SHIELDS_DOWN",
        "ABILITY_RKS_SYSTEM", "ABILITY_COMATOSE", "ABILITY_MULTITYPE", "ABILITY_ICE_FACE", "ABILITY_GULP_MISSILE", "ABILITY_HUNGER_SWITCH", "ABILITY_AS_ONE_ICE_RIDER",
        "ABILITY_AS_ONE_SHADOW_RIDER", "ABILITY_ZERO_TO_HERO", "ABILITY_COMMANDER", "ABILITY_TERA_SHIFT", "ABILITY_TERA_SHELL", "ABILITY_TERAFORM_ZERO", "ABILITY_POISON_PUPPETEER"];

    public static int SeedNumber(string seed)
    {
        // a stable hash (string.GetHashCode changes between runs)
        unchecked
        {
            var h = 216613626;
            foreach (var c in seed)
            {
                h = (h ^ c) * 16777619;
            }

            return h & 0x7FFFFFFF;
        }
    }

    public static string NewSeed() => Random.Shared.Next(100000, 999999).ToString(System.Globalization.CultureInfo.InvariantCulture);

    public static (IChange? Change, RandomizerSummary Summary) Build(GameData data, RandomizerOptions options)
    {
        var rng = new Random(SeedNumber(options.Seed));
        var parts = new List<IChange>();
        var pool = Pool(data, options.OfficialOnly).ToList();
        if (pool.Count == 0)
        {
            return (null, new RandomizerSummary(0, 0, 0, 0, 0, 0));
        }

        // types first: strength-and-type matching below uses the new types
        var species = data.Species.ToList();
        var types = options.Types ? RandomTypes(data, species, rng) : 0;
        var abilities = options.Abilities ? RandomAbilities(data, species, rng) : 0;
        for (var i = 0; i < species.Count; i++)
        {
            if (!ReferenceEquals(species[i], data.Species[i]))
            {
                var index = i;
                parts.Add(new ValueChange<Species>("Pokémon", species[i].Key, v => data.Species[index] = v, data.Species[i], species[i]));
            }
        }

        var byKey = species.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var poolNow = pool.Select(p => byKey[p.Key]).ToList();
        var wild = options.Wild == WildRandomizing.Off ? 0 : RandomWild(data, options, poolNow, byKey, rng, parts);
        var trainers = options.Trainers == TrainerRandomizing.Off ? 0 : RandomTrainers(data, options, poolNow, byKey, rng, parts);
        var (learnsets, tms) = RandomMoves(data, options, rng, parts);
        var summary = new RandomizerSummary(wild, trainers, abilities, types, learnsets, tms);
        return (parts.Count == 0 ? null : new CompositeChange("Randomizer", $"Randomized (seed {options.Seed}): {summary}", parts), summary);
    }

    /// <summary>Pokémon that can be handed out: switched on, real (not battle-only forms or Megas) and, if asked, official.</summary>
    public static IEnumerable<Species> Pool(GameData data, bool officialOnly) =>
        data.Species.Where(s => s.IsEnabled && s.Stats.Total > 0 && !s.LockedFields.ContainsKey("*") && TeamBuilder.IsRegularForm(s)
            && (!officialOnly || !data.Species.Any(x => x.NationalDex is not null) || TeamBuilder.IsOfficial(s)));

    private static bool IsLegendary(Species s) =>
        (s.Tags & (SpeciesTags.RestrictedLegendary | SpeciesTags.SubLegendary | SpeciesTags.Mythical | SpeciesTags.UltraBeast | SpeciesTags.Paradox)) != 0;

    /// <summary>A random Pokémon of similar strength (base stat total within 10%, widened until there's some choice).</summary>
    private static Species Similar(IReadOnlyList<Species> pool, Species original, Random rng, Func<Species, bool>? extra = null)
    {
        var total = original.Stats.Total;
        for (var spread = 0.1; spread < 2; spread += 0.1)
        {
            var near = pool.Where(p => p.Key != original.Key && Math.Abs(p.Stats.Total - total) <= total * spread && (extra is null || extra(p))).ToList();
            if (near.Count >= 5 || (spread >= 0.5 && near.Count > 0))
            {
                return near[rng.Next(near.Count)];
            }
        }

        var any = extra is null ? pool : pool.Where(extra).ToList();
        return any.Count > 0 ? any[rng.Next(any.Count)] : pool[rng.Next(pool.Count)];
    }

    private static int RandomWild(GameData data, RandomizerOptions o, List<Species> pool, Dictionary<string, Species> byKey, Random rng, List<IChange> parts)
    {
        var candidates = o.WildLegendaries ? pool : pool.Where(p => !IsLegendary(p)).ToList();
        if (candidates.Count == 0)
        {
            return 0;
        }

        var count = 0;
        for (var t = 0; t < data.Encounters.Count; t++)
        {
            var table = data.Encounters[t];
            if (table.LockedReason is not null)
            {
                continue;
            }

            var mapping = new Dictionary<string, string>(StringComparer.Ordinal);
            string Pick(string original)
            {
                if (o.WildSameInArea && mapping.TryGetValue(original, out var same))
                {
                    return same;
                }

                var picked = o.Wild == WildRandomizing.SimilarStrength && byKey.TryGetValue(original, out var sp)
                    ? Similar(candidates, sp, rng).Key
                    : candidates[rng.Next(candidates.Count)].Key;
                mapping[original] = picked;
                return picked;
            }

            var methods = table.Methods.Select(m => m with { Slots = m.Slots.Select(s => s.Species is "" or "SPECIES_NONE" ? s : s with { Species = Pick(s.Species) }).ToList() }).ToList();
            var after = table with { Methods = methods };
            count += table.Methods.Zip(methods).Sum(p => p.First.Slots.Zip(p.Second.Slots).Count(s => s.First.Species != s.Second.Species));
            var index = t;
            parts.Add(new ValueChange<EncounterTable>("Wild encounters", table.Key, v => data.Encounters[index] = v, table, after));
        }

        return count;
    }

    private static int RandomTrainers(GameData data, RandomizerOptions o, List<Species> pool, Dictionary<string, Species> byKey, Random rng, List<IChange> parts)
    {
        var candidates = o.TrainerLegendaries ? pool : pool.Where(p => !IsLegendary(p)).ToList();
        var count = 0;
        for (var t = 0; t < data.Trainers.Count; t++)
        {
            var trainer = data.Trainers[t];
            if (trainer.LockedReason is not null || trainer.Party.Count == 0)
            {
                continue;
            }

            var used = new HashSet<string>(StringComparer.Ordinal);
            var party = trainer.Party.Select(mon =>
            {
                if (!byKey.TryGetValue(mon.Species, out var original))
                {
                    return mon;
                }

                bool Fits(Species s) => !used.Contains(s.Key) && (o.Trainers != TrainerRandomizing.KeepTypes || s.Types.Intersect(original.Types).Any());
                var picked = Similar(candidates, original, rng, Fits);
                used.Add(picked.Key);
                // its old moves and ability belong to the old Pokémon: let the game pick level-up moves and a normal ability
                return mon with { Species = picked.Key, Moves = [], Ability = null };
            }).ToList();
            count += party.Count;
            var after = trainer with { Party = party };
            var index = t;
            parts.Add(new ValueChange<Trainer>("Trainers", trainer.Key, v => data.Trainers[index] = v, trainer, after));
        }

        return count;
    }

    /// <summary>Evolution families (a Pokémon and everything it evolves from or into) get the same new types.</summary>
    private static int RandomTypes(GameData data, List<Species> species, Random rng)
    {
        var typeKeys = data.Catalog.Types.Select(t => t.Key).Where(k => k is not ("TYPE_NONE" or "TYPE_MYSTERY" or "TYPE_STELLAR" or "")).ToList();
        if (typeKeys.Count < 2)
        {
            return 0;
        }

        var family = Families(data);
        var newTypes = new Dictionary<int, IReadOnlyList<string>>();
        var count = 0;
        for (var i = 0; i < species.Count; i++)
        {
            var s = species[i];
            if (s.Stats.Total == 0 || s.LockedFields.ContainsKey("*") || s.LockedFields.ContainsKey("Types") || !family.TryGetValue(s.Key, out var f))
            {
                continue;
            }

            if (!newTypes.TryGetValue(f, out var types))
            {
                var first = typeKeys[rng.Next(typeKeys.Count)];
                var second = rng.Next(2) == 0 ? null : typeKeys[rng.Next(typeKeys.Count)];
                newTypes[f] = types = second is null || second == first ? [first] : [first, second];
            }

            species[i] = s with { Types = types };
            count++;
        }

        return count;
    }

    private static Dictionary<string, int> Families(GameData data)
    {
        var parent = data.Species.ToDictionary(s => s.Key, s => s.Key, StringComparer.Ordinal);
        string Find(string k) => parent[k] == k ? k : parent[k] = Find(parent[k]);
        foreach (var set in data.Evolutions)
        {
            foreach (var e in set.Evolutions)
            {
                if (parent.ContainsKey(set.FromSpecies) && parent.ContainsKey(e.TargetSpecies))
                {
                    parent[Find(e.TargetSpecies)] = Find(set.FromSpecies);
                }
            }
        }

        var ids = new Dictionary<string, int>(StringComparer.Ordinal);
        return data.Species.ToDictionary(s => s.Key, s => ids.TryGetValue(Find(s.Key), out var id) ? id : ids[Find(s.Key)] = ids.Count, StringComparer.Ordinal);
    }

    private static int RandomAbilities(GameData data, List<Species> species, Random rng)
    {
        var abilities = data.Catalog.Abilities.Select(a => a.Key).Where(a => !BannedAbilities.Contains(a) && a.Length > 0).ToList();
        if (abilities.Count == 0)
        {
            return 0;
        }

        var count = 0;
        for (var i = 0; i < species.Count; i++)
        {
            var s = species[i];
            if (s.Stats.Total == 0 || s.LockedFields.ContainsKey("*") || s.LockedFields.ContainsKey("Abilities") || s.Abilities.Count == 0)
            {
                continue;
            }

            // keep the number of slots; empty slots stay empty
            var picked = new List<string>();
            foreach (var slot in s.Abilities)
            {
                if (slot is "" or "ABILITY_NONE")
                {
                    picked.Add(slot);
                    continue;
                }

                string next;
                do
                {
                    next = abilities[rng.Next(abilities.Count)];
                }
                while (picked.Contains(next) && abilities.Count > picked.Count);
                picked.Add(next);
            }

            species[i] = s with { Abilities = picked };
            count++;
        }

        return count;
    }

    private static (int Learnsets, int Tms) RandomMoves(GameData data, RandomizerOptions o, Random rng, List<IChange> parts)
    {
        if (!o.LevelUpMoves && !o.TmCompatibility)
        {
            return (0, 0);
        }

        var moves = data.Moves.Where(m => m.Key is not ("MOVE_NONE" or "MOVE_STRUGGLE" or "")).ToList();
        var weakAttacks = moves.Where(m => m.Category != MoveCategory.Status && m.Power is > 0 and <= 50).ToList();
        var teachable = data.Learnsets.SelectMany(l => l.Teachable).Distinct(StringComparer.Ordinal).ToList();
        int learnsets = 0, tms = 0;
        for (var i = 0; i < data.Learnsets.Count; i++)
        {
            var l = data.Learnsets[i];
            var after = l;
            if (o.LevelUpMoves && l.LevelUp.Count > 0 && moves.Count > 0 && !l.LockedParts.ContainsKey("levelup"))
            {
                // same levels, new moves; the first move is always a weak attack so the Pokémon can fight
                var list = l.LevelUp.Select(m => m with { Move = moves[rng.Next(moves.Count)].Key }).ToList();
                if (weakAttacks.Count > 0)
                {
                    list[0] = list[0] with { Move = weakAttacks[rng.Next(weakAttacks.Count)].Key };
                }

                after = after with { LevelUp = list };
                learnsets++;
            }

            if (o.TmCompatibility && teachable.Count > 0 && !l.LockedParts.ContainsKey("teachable"))
            {
                after = after with { Teachable = teachable.Where(_ => rng.Next(2) == 0).ToList() };
                tms++;
            }

            if (!ReferenceEquals(after, l))
            {
                var index = i;
                parts.Add(new ValueChange<Learnset>("Learnsets", l.Key, v => data.Learnsets[index] = v, l, after));
            }
        }

        return (learnsets, tms);
    }
}
