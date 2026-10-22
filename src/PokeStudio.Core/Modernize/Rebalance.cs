using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Modernize;

/// <summary>Which list a rebalance change is in.</summary>
public enum RebalanceGroup
{
    /// <summary>Abilities that hold their own Pokémon back (Truant, Defeatist, Klutz, Stall, Wimp Out…).</summary>
    HarmfulAbility,

    /// <summary>Weak fully evolved Pokémon the community often asks to be buffed.</summary>
    Buff,
}

/// <summary>One change to a Pokémon: new abilities, types and/or base stats.</summary>
public sealed record RebalanceChange(string Species, RebalanceGroup Group, string Why)
{
    /// <summary>Abilities to replace: old ability → new one (in every slot that has the old one).</summary>
    public IReadOnlyDictionary<string, string> AbilitySwaps { get; init; } = new Dictionary<string, string>();

    /// <summary>All three ability slots, when the whole set changes (normal 1, normal 2, hidden).</summary>
    public IReadOnlyList<string>? Abilities { get; init; }

    public IReadOnlyList<string>? Types { get; init; }

    /// <summary>New base stats (null: unchanged); <see cref="StatBoost"/> adds to the current ones instead.</summary>
    public BaseStats? Stats { get; init; }
    public BaseStats? StatBoost { get; init; }
}

/// <summary>A change as the preview shows it, with the moves its new abilities bring.</summary>
public sealed record RebalanceRow(RebalanceChange Change, string Name, string Summary, IReadOnlyList<string> MoveNotes);

/// <summary>
/// The Rebalance tool: abilities that hold their own Pokémon back are replaced (Regigigas keeps Slow Start), Slaking
/// loses Truant for a pseudo-legendary stat line, and weak Pokémon can get a buff. A Pokémon that gets an ability it
/// can't make use of (Iron Fist without punches) also gets the moves for it: by TM when a TM teaches the move,
/// otherwise by level-up at a level that fits the move's power.
/// </summary>
public static class Rebalance
{
    private const string A = "ABILITY_";
    private const string S = "SPECIES_";

    private static Dictionary<string, string> Swap(string from, string to) => new() { [A + from] = A + to };

    private static BaseStats Boost(int hp = 0, int atk = 0, int def = 0, int spa = 0, int spd = 0, int spe = 0) => new(hp, atk, def, spa, spd, spe);

    /// <summary>The ready-made changes (only the ones whose Pokémon the game has are offered).</summary>
    public static IReadOnlyList<RebalanceChange> Presets { get; } =
    [
        // abilities that hold their own Pokémon back
        new(S + "SLAKOTH", RebalanceGroup.HarmfulAbility, "Truant: it only acts every other turn.") { AbilitySwaps = Swap("TRUANT", "RUN_AWAY") },
        new(S + "SLAKING", RebalanceGroup.HarmfulAbility, "Truant: it only acts every other turn. Without it Slaking's stats would beat legendaries, so they come down to a strong pseudo-legendary's (600).")
        {
            Abilities = [A + "GUTS", A + "SCRAPPY", A + "MOXIE"],
            Stats = new BaseStats(150, 140, 90, 75, 65, 80),
        },
        new(S + "DURANT", RebalanceGroup.HarmfulAbility, "Truant (hidden ability).") { AbilitySwaps = Swap("TRUANT", "HUSTLE") },
        new(S + "ARCHEN", RebalanceGroup.HarmfulAbility, "Defeatist: its attacks are halved below half HP.") { AbilitySwaps = Swap("DEFEATIST", "ROCK_HEAD") },
        new(S + "ARCHEOPS", RebalanceGroup.HarmfulAbility, "Defeatist: its attacks are halved below half HP.") { AbilitySwaps = Swap("DEFEATIST", "ROCK_HEAD") },
        new(S + "BUNEARY", RebalanceGroup.HarmfulAbility, "Klutz: it can't use held items.") { AbilitySwaps = Swap("KLUTZ", "RUN_AWAY") },
        new(S + "LOPUNNY", RebalanceGroup.HarmfulAbility, "Klutz: it can't use held items.") { AbilitySwaps = Swap("KLUTZ", "SCRAPPY") },
        new(S + "WOOBAT", RebalanceGroup.HarmfulAbility, "Klutz: it can't use held items.") { AbilitySwaps = Swap("KLUTZ", "UNAWARE") },
        new(S + "SWOOBAT", RebalanceGroup.HarmfulAbility, "Klutz: it can't use held items.") { AbilitySwaps = Swap("KLUTZ", "UNAWARE") },
        new(S + "GOLETT", RebalanceGroup.HarmfulAbility, "Klutz: it can't use held items.") { AbilitySwaps = Swap("KLUTZ", "IRON_FIST") },
        new(S + "GOLURK", RebalanceGroup.HarmfulAbility, "Klutz: it can't use held items.") { AbilitySwaps = Swap("KLUTZ", "IRON_FIST") },
        new(S + "AUDINO", RebalanceGroup.HarmfulAbility, "Klutz (hidden ability): it can't use held items.") { AbilitySwaps = Swap("KLUTZ", "REGENERATOR") },
        new(S + "SABLEYE", RebalanceGroup.HarmfulAbility, "Stall: it always moves last.") { AbilitySwaps = Swap("STALL", "PRANKSTER") },
        new(S + "WIMPOD", RebalanceGroup.HarmfulAbility, "Wimp Out: it flees or switches out by itself at half HP.") { AbilitySwaps = Swap("WIMP_OUT", "SWIFT_SWIM") },
        new(S + "GOLISOPOD", RebalanceGroup.HarmfulAbility, "Emergency Exit: it switches out by itself at half HP.") { AbilitySwaps = Swap("EMERGENCY_EXIT", "SHELL_ARMOR") },

        // weak Pokémon the community often asks to be buffed (offered unticked: the hack's creator decides)
        new(S + "FARFETCHD", RebalanceGroup.Buff, "Very weak stats; its leek is a fighting tool.") { Types = ["TYPE_FIGHTING", "TYPE_FLYING"], StatBoost = Boost(atk: 20, spe: 20) },
        new(S + "DELCATTY", RebalanceGroup.Buff, "Total 400 and plain Normal type.") { Types = ["TYPE_NORMAL", "TYPE_FAIRY"], StatBoost = Boost(hp: 10, spa: 25, spd: 15) },
        new(S + "LUVDISC", RebalanceGroup.Buff, "Total 330, the weakest fully evolved Water type.") { Types = ["TYPE_WATER", "TYPE_FAIRY"], StatBoost = Boost(hp: 25, def: 20, spa: 25, spd: 20, spe: 10) },
        new(S + "LEDIAN", RebalanceGroup.Buff, "Fragile, with Iron Fist and no Attack to use it.") { Types = ["TYPE_BUG", "TYPE_FIGHTING"], StatBoost = Boost(atk: 45, spe: 10) },
        new(S + "BEAUTIFLY", RebalanceGroup.Buff, "A weak early Bug type.") { StatBoost = Boost(spa: 20, spe: 15) },
        new(S + "DUSTOX", RebalanceGroup.Buff, "A weak early Bug type.") { StatBoost = Boost(def: 10, spa: 15, spd: 10) },
        new(S + "MASQUERAIN", RebalanceGroup.Buff, "Loses its Water type when it evolves.") { Types = ["TYPE_BUG", "TYPE_WATER"], StatBoost = Boost(spa: 10, spe: 10) },
        new(S + "MIGHTYENA", RebalanceGroup.Buff, "An early route Pokémon that falls off.") { StatBoost = Boost(atk: 15, spe: 15) },
        new(S + "LINOONE", RebalanceGroup.Buff, "An early route Pokémon that falls off.") { StatBoost = Boost(atk: 15, spe: 10) },
        new(S + "PLUSLE", RebalanceGroup.Buff, "A weak pair Pokémon.") { StatBoost = Boost(spa: 15, spe: 10) },
        new(S + "MINUN", RebalanceGroup.Buff, "A weak pair Pokémon.") { StatBoost = Boost(spa: 15, spe: 10) },
        new(S + "VOLBEAT", RebalanceGroup.Buff, "A weak pair Pokémon.") { StatBoost = Boost(atk: 15, spe: 10) },
        new(S + "ILLUMISE", RebalanceGroup.Buff, "A weak pair Pokémon.") { StatBoost = Boost(spa: 15, spe: 10) },
        new(S + "CASTFORM", RebalanceGroup.Buff, "Forecast is fun, but its stats are too low to use it.") { StatBoost = Boost(10, 10, 10, 10, 10, 10) },
        new(S + "KECLEON", RebalanceGroup.Buff, "Slow and gimmicky; Protean only as hidden ability.") { AbilitySwaps = Swap("COLOR_CHANGE", "PROTEAN") },
        new(S + "SEVIPER", RebalanceGroup.Buff, "A mediocre rival to Zangoose.") { Types = ["TYPE_POISON", "TYPE_DARK"], StatBoost = Boost(atk: 10, spa: 10, spe: 10) },
        new(S + "ZANGOOSE", RebalanceGroup.Buff, "A mediocre rival to Seviper.") { StatBoost = Boost(hp: 10, def: 10) },
        new(S + "WIGGLYTUFF", RebalanceGroup.Buff, "An early Pokémon that falls off.") { StatBoost = Boost(def: 15, spa: 15, spe: 10) },
        new(S + "RATICATE", RebalanceGroup.Buff, "An early Pokémon that falls off.") { StatBoost = Boost(hp: 10, atk: 15, def: 15) },
        new(S + "FURRET", RebalanceGroup.Buff, "An early Pokémon that falls off.") { StatBoost = Boost(atk: 20, def: 10, spe: 10) },
        new(S + "NOCTOWL", RebalanceGroup.Buff, "An early Pokémon that falls off.") { StatBoost = Boost(def: 15, spa: 20, spe: 5) },
    ];

    /// <summary>Abilities and the moves that make them work.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> AbilityMoves { get; } = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal)
    {
        [A + "IRON_FIST"] = ["MOVE_MACH_PUNCH", "MOVE_DRAIN_PUNCH", "MOVE_FIRE_PUNCH", "MOVE_ICE_PUNCH", "MOVE_THUNDER_PUNCH", "MOVE_BULLET_PUNCH", "MOVE_POWER_UP_PUNCH"],
        [A + "STRONG_JAW"] = ["MOVE_BITE", "MOVE_CRUNCH", "MOVE_FIRE_FANG", "MOVE_ICE_FANG", "MOVE_THUNDER_FANG", "MOVE_PSYCHIC_FANGS"],
        [A + "MEGA_LAUNCHER"] = ["MOVE_WATER_PULSE", "MOVE_DARK_PULSE", "MOVE_DRAGON_PULSE", "MOVE_AURA_SPHERE"],
        [A + "SHARPNESS"] = ["MOVE_CUT", "MOVE_NIGHT_SLASH", "MOVE_X_SCISSOR", "MOVE_LEAF_BLADE", "MOVE_PSYCHO_CUT"],
        [A + "RECKLESS"] = ["MOVE_TAKE_DOWN", "MOVE_DOUBLE_EDGE"],
        [A + "ROCK_HEAD"] = ["MOVE_TAKE_DOWN", "MOVE_DOUBLE_EDGE", "MOVE_HEAD_SMASH"],
        [A + "PUNK_ROCK"] = ["MOVE_HYPER_VOICE", "MOVE_BUG_BUZZ", "MOVE_BOOMBURST"],
    };

    /// <summary>The level at which a move of this power is fair for a trainer's Pokémon (the lowest level that allows it).</summary>
    public static int FairLevel(int power)
    {
        for (var level = 1; level <= 100; level++)
        {
            if (MoveRules.MaxPower(level, Difficulty.Normal) >= power)
            {
                return Math.Max(level, 5);
            }
        }

        return 61;
    }

    /// <summary>The preview rows for the changes whose Pokémon the game has.</summary>
    public static List<RebalanceRow> Rows(GameData data)
    {
        var rows = new List<RebalanceRow>();
        foreach (var c in Presets)
        {
            if (data.Species.FirstOrDefault(s => s.Key == c.Species) is not { } species || After(species, c) is not { } after || after == species)
            {
                continue;
            }

            var parts = new List<string>();
            if (!after.Abilities.SequenceEqual(species.Abilities))
            {
                parts.Add($"Abilities: {Names(data, species.Abilities)} → {Names(data, after.Abilities)}");
            }

            if (!after.Types.SequenceEqual(species.Types))
            {
                parts.Add($"Type: {string.Join("/", species.Types.Distinct().Select(data.Catalog.NameOf))} → {string.Join("/", after.Types.Distinct().Select(data.Catalog.NameOf))}");
            }

            if (after.Stats != species.Stats)
            {
                parts.Add($"Stats: {Line(species.Stats)} → {Line(after.Stats)}");
            }

            var moves = MovesFor(data, species, after).Select(m => m.Level == 0
                ? $"{data.Moves.FirstOrDefault(x => x.Key == m.Move)?.Name ?? m.Move} by TM"
                : $"{data.Moves.FirstOrDefault(x => x.Key == m.Move)?.Name ?? m.Move} at Lv {m.Level}").ToList();
            rows.Add(new RebalanceRow(c, species.DisplayName, string.Join(" · ", parts), moves));
        }

        return rows;
    }

    private static string Line(BaseStats s) => $"{s.Hp}/{s.Attack}/{s.Defense}/{s.SpAttack}/{s.SpDefense}/{s.Speed} ({s.Total})";

    private static string Names(GameData data, IReadOnlyList<string> abilities) =>
        string.Join(", ", abilities.Where(a => a.Length > 0 && a != "ABILITY_NONE").Distinct().Select(data.Catalog.NameOf));

    /// <summary>The Pokémon after the change (null when a field can't be changed here).</summary>
    private static Species? After(Species s, RebalanceChange c)
    {
        if (s.LockedFields.ContainsKey("*"))
        {
            return null;
        }

        var after = s;
        if (c.Abilities is { } all && !s.LockedFields.ContainsKey("Abilities"))
        {
            after = after with { Abilities = all };
        }

        if (c.AbilitySwaps.Count > 0 && !s.LockedFields.ContainsKey("Abilities"))
        {
            after = after with { Abilities = after.Abilities.Select(a => c.AbilitySwaps.GetValueOrDefault(a, a)).ToList() };
        }

        if (c.Types is { } types && !s.LockedFields.ContainsKey("Types"))
        {
            after = after with { Types = types };
        }

        if (!s.LockedFields.ContainsKey("Stats"))
        {
            if (c.Stats is { } stats)
            {
                after = after with { Stats = stats };
            }
            else if (c.StatBoost is { } b)
            {
                var o = s.Stats;
                int Cap(int v) => Math.Min(v, BaseStats.Max);
                after = after with { Stats = new BaseStats(Cap(o.Hp + b.Hp), Cap(o.Attack + b.Attack), Cap(o.Defense + b.Defense), Cap(o.SpAttack + b.SpAttack), Cap(o.SpDefense + b.SpDefense), Cap(o.Speed + b.Speed)) };
            }
        }

        return after;
    }

    /// <summary>Moves its abilities need that the changed Pokémon can't learn yet: (move, 0 = by TM, or a level).</summary>
    private static List<(string Move, int Level)> MovesFor(GameData data, Species before, Species after)
    {
        var result = new List<(string Move, int Level)>();
        if (data.LearnsetFor(before) is not { } learnset)
        {
            return result;
        }

        var tms = data.Catalog.Machines.Select(m => m.Move).ToHashSet(StringComparer.Ordinal);
        var leftOut = data.Lab?.LeftOutMoves ?? [];
        var known = learnset.LevelUp.Select(m => m.Move).Concat(learnset.Teachable).Concat(learnset.EggMoves).ToHashSet(StringComparer.Ordinal);
        // every ability of a changed Pokémon (Ledian already has Iron Fist, but no punches to use it with)
        foreach (var ability in after.Abilities.Distinct())
        {
            foreach (var key in AbilityMoves.GetValueOrDefault(ability) ?? [])
            {
                if (known.Contains(key) || leftOut.Contains(key) || result.Any(r => r.Move == key) || data.Moves.FirstOrDefault(m => m.Key == key) is not { } move)
                {
                    continue;
                }

                result.Add((key, tms.Contains(key) && !learnset.LockedParts.ContainsKey("teachable") ? 0 : FairLevel(move.Power)));
            }
        }

        return result;
    }

    /// <summary>The chosen changes as one undo step (null when nothing changes).</summary>
    public static IChange? Build(GameData data, IEnumerable<RebalanceChange> chosen)
    {
        var parts = new List<IChange>();
        var learnsetsDone = new HashSet<string>(StringComparer.Ordinal);
        foreach (var c in chosen)
        {
            var index = data.Species.FindIndex(s => s.Key == c.Species);
            if (index < 0 || After(data.Species[index], c) is not { } after || after == data.Species[index])
            {
                continue;
            }

            var before = data.Species[index];
            parts.Add(new ValueChange<Species>("Pokémon", before.DisplayName, v => data.Species[index] = v, before, after));

            // the moves its new abilities need (a learnset shared by several forms is changed once)
            var moves = MovesFor(data, before, after);
            var li = data.LearnsetFor(before) is { } l ? data.Learnsets.FindIndex(x => x.Key == l.Key) : -1;
            if (moves.Count > 0 && li >= 0 && learnsetsDone.Add(data.Learnsets[li].Key))
            {
                var old = data.Learnsets[li];
                var levelUp = old.LockedParts.ContainsKey("levelup") ? old.LevelUp
                    : old.LevelUp.Concat(moves.Where(m => m.Level > 0).Select(m => new LevelUpMove(m.Level, m.Move))).OrderBy(m => m.Level).ToList();
                var teachable = old.Teachable.Concat(moves.Where(m => m.Level == 0).Select(m => m.Move)).Distinct().ToList();
                var newSet = old with { LevelUp = levelUp, Teachable = teachable };
                parts.Add(new ValueChange<Learnset>("Learnsets", old.Key, v => data.Learnsets[li] = v, old, newSet));
            }
        }

        return parts.Count == 0 ? null : new CompositeChange("Pokémon", $"Rebalance {parts.OfType<ValueChange<Species>>().Count()} Pokémon", parts);
    }
}
