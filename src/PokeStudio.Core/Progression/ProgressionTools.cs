using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Progression;

/// <summary>A point on the level curve: what used to be <paramref name="From"/> becomes <paramref name="To"/>.</summary>
public sealed record CurvePoint(int From, int To);

/// <summary>
/// Reshapes levels along milestones (e.g. Gym 8 ace 46 → 70, Champion 58 → 95), keeping every trainer and wild
/// Pokémon in the same order relative to the milestones around it.
/// </summary>
public static class LevelCurve
{
    /// <summary>Piecewise-linear map through the points (plus 1 → 1); beyond the last point the last slope continues.</summary>
    public static int Map(int level, IReadOnlyList<CurvePoint> points, int maxLevel)
    {
        var pts = new List<CurvePoint> { new(1, 1) };
        pts.AddRange(points.Where(p => p.From > 1).OrderBy(p => p.From).DistinctBy(p => p.From));
        if (pts.Count == 1)
        {
            return Math.Clamp(level, 1, maxLevel);
        }

        CurvePoint a, b;
        var i = pts.FindIndex(p => p.From >= level);
        if (i == 0)
        {
            return Math.Clamp(pts[0].To, 1, maxLevel);
        }

        if (i < 0)
        {
            (a, b) = (pts[^2], pts[^1]);
        }
        else
        {
            (a, b) = (pts[i - 1], pts[i]);
        }

        var t = (level - a.From) / (double)(b.From - a.From);
        var mapped = a.To + (t * (b.To - a.To));
        return Math.Clamp((int)Math.Round(mapped, MidpointRounding.AwayFromZero), 1, maxLevel);
    }

    /// <summary>The curve from the game's current caps to new target levels (same milestones, same order).</summary>
    public static IReadOnlyList<CurvePoint> FromCaps(IReadOnlyList<LevelCap> current, IReadOnlyList<int> targets) =>
        current.Zip(targets).Select(p => new CurvePoint(p.First.Level, p.Second)).ToList();

    public sealed record Options(bool Trainers = true, bool Wild = true, bool UpdateCaps = true, bool EvolveTrainers = false, int ItemEvolutionLevel = 40);

    /// <summary>Applies the curve as one undoable change. Returns null if nothing would change.</summary>
    public static IChange? Apply(GameData data, IReadOnlyList<CurvePoint> curve, IReadOnlyList<int> newCaps, Options options, GameLimits limits, string description)
    {
        var parts = new List<IChange>();
        int Map2(int l) => Map(l, curve, limits.MaxLevel);

        if (options.Trainers)
        {
            for (var i = 0; i < data.Trainers.Count; i++)
            {
                var t = data.Trainers[i];
                if (t.LockedReason is not null || t.Party.Count == 0)
                {
                    continue;
                }

                var party = t.Party.Select(p => p with { Level = Map2(p.Level) }).ToList();
                if (options.EvolveTrainers)
                {
                    party = party.Select(p => p with { Species = EvolutionPaths.EvolvedForm(data, p.Species, p.Level, options.ItemEvolutionLevel) }).ToList();
                }

                if (party.Zip(t.Party).All(x => x.First.Equivalent(x.Second)))
                {
                    continue;
                }

                var index = i;
                parts.Add(new ValueChange<Trainer>("Trainers", t.Name, v => data.Trainers[index] = v, t, t with { Party = party }));
            }
        }

        if (options.Wild)
        {
            for (var i = 0; i < data.Encounters.Count; i++)
            {
                var e = data.Encounters[i];
                if (e.LockedReason is not null)
                {
                    continue;
                }

                var methods = e.Methods.Select(m => m with { Slots = m.Slots.Select(s => s with { MinLevel = Map2(s.MinLevel), MaxLevel = Map2(s.MaxLevel) }).ToList() }).ToList();
                if (methods.Zip(e.Methods).All(x => x.First.Slots.SequenceEqual(x.Second.Slots)))
                {
                    continue;
                }

                var index = i;
                parts.Add(new ValueChange<EncounterTable>("Wild encounters", e.Key, v => data.Encounters[index] = v, e, e with { Methods = methods }));
            }
        }

        if (options.UpdateCaps && data.Rules is { } rules && newCaps.Count == rules.LevelCaps.Count)
        {
            var caps = rules.LevelCaps.Zip(newCaps).Select(p => p.First with { Level = p.Second }).ToList();
            if (!caps.SequenceEqual(rules.LevelCaps))
            {
                parts.Add(new ValueChange<GameRules?>("Level caps", "caps", v => data.Rules = v, rules, rules with { LevelCaps = caps }));
            }
        }

        return parts.Count == 0 ? null : new CompositeChange("Progression", description, parts);
    }
}

public static class EvolutionPaths
{
    /// <summary>
    /// What <paramref name="species"/> would have evolved into by <paramref name="level"/>: level evolutions when the level is
    /// reached; item, trade and friendship evolutions once the level reaches <paramref name="otherMethodsLevel"/>.
    /// Branches with extra conditions are skipped (the first plain branch is used).
    /// </summary>
    public static string EvolvedForm(GameData data, string species, int level, int otherMethodsLevel)
    {
        var current = species;
        for (var guard = 0; guard < 4; guard++)
        {
            var set = data.Evolutions.FirstOrDefault(e => e.FromSpecies == current);
            if (set is null || set.Evolutions.Count == 0)
            {
                break;
            }

            var next = set.Evolutions.FirstOrDefault(e => e.Conditions.Count == 0 && e.Method == "EVO_LEVEL"
                    && int.TryParse(e.Parameter, out var at) && at > 0 && level >= at)
                ?? (level >= otherMethodsLevel
                    ? set.Evolutions.FirstOrDefault(e => e.Method is "EVO_ITEM" or "EVO_TRADE"
                        || (e.Method == "EVO_LEVEL" && e.Parameter == "0" && e.Conditions.All(c => c.Kind is "IF_MIN_FRIENDSHIP")))
                    : null);
            if (next is null || data.Species.All(s => s.Key != next.TargetSpecies || !s.IsEnabled))
            {
                break;
            }

            current = next.TargetSpecies;
        }

        return current;
    }
}

public static class WildTools
{
    /// <summary>Adds <paramref name="amount"/> levels to every slot of the chosen tables, as one undo step.</summary>
    public static IChange? ChangeLevels(GameData data, IEnumerable<string> tableKeys, int amount, GameLimits limits)
    {
        var keys = tableKeys.ToHashSet(StringComparer.Ordinal);
        var parts = new List<IChange>();
        for (var i = 0; i < data.Encounters.Count; i++)
        {
            var e = data.Encounters[i];
            if (!keys.Contains(e.Key) || e.LockedReason is not null)
            {
                continue;
            }

            int L(int x) => Math.Clamp(x + amount, 1, limits.MaxLevel);
            var methods = e.Methods.Select(m => m with { Slots = m.Slots.Select(s => s with { MinLevel = L(s.MinLevel), MaxLevel = L(s.MaxLevel) }).ToList() }).ToList();
            var index = i;
            parts.Add(new ValueChange<EncounterTable>("Wild encounters", e.Key, v => data.Encounters[index] = v, e, e with { Methods = methods }));
        }

        return parts.Count == 0 ? null : new CompositeChange("Wild encounters", $"{parts.Count} areas · wild levels {amount:+0;-0}", parts);
    }

    /// <summary>Replaces one Pokémon with another in wild slots (and optionally trainer teams), as one undo step.</summary>
    public static IChange? Replace(GameData data, string from, string to, bool trainers, string fromName, string toName)
    {
        var parts = new List<IChange>();
        var slots = 0;
        for (var i = 0; i < data.Encounters.Count; i++)
        {
            var e = data.Encounters[i];
            if (e.LockedReason is not null || e.Methods.All(m => m.Slots.All(s => s.Species != from)))
            {
                continue;
            }

            slots += e.Methods.Sum(m => m.Slots.Count(s => s.Species == from));
            var methods = e.Methods.Select(m => m with { Slots = m.Slots.Select(s => s.Species == from ? s with { Species = to } : s).ToList() }).ToList();
            var index = i;
            parts.Add(new ValueChange<EncounterTable>("Wild encounters", e.Key, v => data.Encounters[index] = v, e, e with { Methods = methods }));
        }

        if (trainers)
        {
            for (var i = 0; i < data.Trainers.Count; i++)
            {
                var t = data.Trainers[i];
                if (t.LockedReason is not null || t.Party.All(p => p.Species != from))
                {
                    continue;
                }

                slots += t.Party.Count(p => p.Species == from);
                var index = i;
                parts.Add(new ValueChange<Trainer>("Trainers", t.Name, v => data.Trainers[index] = v, t,
                    t with { Party = t.Party.Select(p => p.Species == from ? p with { Species = to, Ability = null } : p).ToList() }));
            }
        }

        return parts.Count == 0 ? null : new CompositeChange("Wild encounters", $"{fromName} → {toName} ({slots} places)", parts);
    }
}

/// <summary>Ready-made shop stock. Only items the game actually has are used.</summary>
public sealed record MartPreset(string Id, string Name, string Description, IReadOnlyList<string> Items);

public static class MartPresets
{
    private static readonly MartPreset[] All =
    [
        new("competitive", "Competitive held items", "The items strong players build around.",
        [
            "ITEM_CHOICE_SCARF", "ITEM_CHOICE_SPECS", "ITEM_CHOICE_BAND", "ITEM_LIFE_ORB", "ITEM_LEFTOVERS", "ITEM_FOCUS_SASH",
            "ITEM_ASSAULT_VEST", "ITEM_ROCKY_HELMET", "ITEM_EVIOLITE", "ITEM_HEAVY_DUTY_BOOTS", "ITEM_WEAKNESS_POLICY", "ITEM_EXPERT_BELT",
            "ITEM_LIGHT_CLAY", "ITEM_BLACK_SLUDGE", "ITEM_AIR_BALLOON", "ITEM_TOXIC_ORB", "ITEM_FLAME_ORB", "ITEM_WHITE_HERB",
            "ITEM_MENTAL_HERB", "ITEM_POWER_HERB", "ITEM_EJECT_BUTTON", "ITEM_RED_CARD", "ITEM_SCOPE_LENS", "ITEM_WIDE_LENS",
        ]),
        new("berries", "Battle berries", "Berries that heal or protect in battle.",
        [
            "ITEM_SITRUS_BERRY", "ITEM_LUM_BERRY", "ITEM_ORAN_BERRY", "ITEM_CHESTO_BERRY", "ITEM_PECHA_BERRY", "ITEM_RAWST_BERRY",
            "ITEM_ASPEAR_BERRY", "ITEM_CHERI_BERRY", "ITEM_LEPPA_BERRY", "ITEM_PERSIM_BERRY", "ITEM_FIGY_BERRY", "ITEM_WIKI_BERRY",
            "ITEM_MAGO_BERRY", "ITEM_AGUAV_BERRY", "ITEM_IAPAPA_BERRY", "ITEM_LIECHI_BERRY", "ITEM_SALAC_BERRY", "ITEM_PETAYA_BERRY",
            "ITEM_GANLON_BERRY", "ITEM_APICOT_BERRY", "ITEM_OCCA_BERRY", "ITEM_PASSHO_BERRY", "ITEM_WACAN_BERRY", "ITEM_RINDO_BERRY",
            "ITEM_YACHE_BERRY", "ITEM_CHOPLE_BERRY", "ITEM_KEBIA_BERRY", "ITEM_SHUCA_BERRY", "ITEM_COBA_BERRY", "ITEM_PAYAPA_BERRY",
            "ITEM_TANGA_BERRY", "ITEM_CHARTI_BERRY", "ITEM_KASIB_BERRY", "ITEM_HABAN_BERRY", "ITEM_COLBUR_BERRY", "ITEM_BABIRI_BERRY",
            "ITEM_CHILAN_BERRY", "ITEM_ROSELI_BERRY",
        ]),
        new("x-items", "X items", "One-use stat boosts in battle.",
            ["ITEM_X_ATTACK", "ITEM_X_DEFENSE", "ITEM_X_SP_ATK", "ITEM_X_SP_DEF", "ITEM_X_SPEED", "ITEM_X_ACCURACY", "ITEM_DIRE_HIT", "ITEM_GUARD_SPEC"]),
        new("training", "Training items", "Change natures, abilities and stats without grinding.",
        [
            "ITEM_RARE_CANDY", "ITEM_ABILITY_CAPSULE", "ITEM_ABILITY_PATCH", "ITEM_BOTTLE_CAP", "ITEM_GOLD_BOTTLE_CAP",
            "ITEM_HP_UP", "ITEM_PROTEIN", "ITEM_IRON", "ITEM_CALCIUM", "ITEM_ZINC", "ITEM_CARBOS", "ITEM_PP_UP", "ITEM_PP_MAX",
            "ITEM_LONELY_MINT", "ITEM_ADAMANT_MINT", "ITEM_NAUGHTY_MINT", "ITEM_BRAVE_MINT", "ITEM_BOLD_MINT", "ITEM_IMPISH_MINT",
            "ITEM_LAX_MINT", "ITEM_RELAXED_MINT", "ITEM_MODEST_MINT", "ITEM_MILD_MINT", "ITEM_RASH_MINT", "ITEM_QUIET_MINT",
            "ITEM_CALM_MINT", "ITEM_GENTLE_MINT", "ITEM_CAREFUL_MINT", "ITEM_SASSY_MINT", "ITEM_TIMID_MINT", "ITEM_HASTY_MINT",
            "ITEM_JOLLY_MINT", "ITEM_NAIVE_MINT", "ITEM_SERIOUS_MINT",
        ]),
        new("healing", "Healing", "Potions and status cures.",
        [
            "ITEM_POTION", "ITEM_SUPER_POTION", "ITEM_HYPER_POTION", "ITEM_MAX_POTION", "ITEM_FULL_RESTORE", "ITEM_REVIVE",
            "ITEM_FULL_HEAL", "ITEM_ANTIDOTE", "ITEM_PARALYZE_HEAL", "ITEM_AWAKENING", "ITEM_BURN_HEAL", "ITEM_ICE_HEAL", "ITEM_ETHER", "ITEM_ELIXIR",
        ]),
        new("balls", "Poké Balls", "Every kind of ball.",
        [
            "ITEM_POKE_BALL", "ITEM_GREAT_BALL", "ITEM_ULTRA_BALL", "ITEM_NET_BALL", "ITEM_DIVE_BALL", "ITEM_NEST_BALL", "ITEM_REPEAT_BALL",
            "ITEM_TIMER_BALL", "ITEM_QUICK_BALL", "ITEM_DUSK_BALL", "ITEM_HEAL_BALL", "ITEM_LUXURY_BALL", "ITEM_PREMIER_BALL",
        ]),
    ];

    public static IReadOnlyList<MartPreset> For(GameCatalog catalog, IEnumerable<string>? extraTms = null)
    {
        var known = catalog.Items.Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        var list = All.Select(p => p with { Items = p.Items.Where(known.Contains).ToList() }).Where(p => p.Items.Count > 0).ToList();
        var tms = catalog.Items.Where(i => i.Key.StartsWith("ITEM_TM", StringComparison.Ordinal) || i.Key.StartsWith("ITEM_HM", StringComparison.Ordinal))
            .Select(i => i.Key).Concat(extraTms ?? []).Distinct().ToList();
        if (tms.Count > 0)
        {
            list.Add(new MartPreset("tms", "TMs", "Every TM (and HM) in the game.", tms));
        }

        return list;
    }
}
