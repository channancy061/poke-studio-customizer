using PokeStudio.Core.Models;

namespace PokeStudio.Core.ChampionLab;

/// <summary>The standard type chart (Gen 6 and later). Types the chart doesn't know count as neutral.</summary>
public static class TypeChart
{
    private static readonly string[] Order =
    [
        "NORMAL", "FIRE", "WATER", "ELECTRIC", "GRASS", "ICE", "FIGHTING", "POISON", "GROUND",
        "FLYING", "PSYCHIC", "BUG", "ROCK", "GHOST", "DRAGON", "DARK", "STEEL", "FAIRY",
    ];

    // rows: attacking type; columns: defending type (same order). 0 = no effect, 1 = half, 2 = normal, 4 = double.
    private static readonly int[][] Chart =
    [
        [2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 1, 0, 2, 2, 1, 2], // Normal
        [2, 1, 1, 2, 4, 4, 2, 2, 2, 2, 2, 4, 1, 2, 1, 2, 4, 2], // Fire
        [2, 4, 1, 2, 1, 2, 2, 2, 4, 2, 2, 2, 4, 2, 1, 2, 2, 2], // Water
        [2, 2, 4, 1, 1, 2, 2, 2, 0, 4, 2, 2, 2, 2, 1, 2, 2, 2], // Electric
        [2, 1, 4, 2, 1, 2, 2, 1, 4, 1, 2, 1, 4, 2, 1, 2, 1, 2], // Grass
        [2, 1, 1, 2, 4, 1, 2, 2, 4, 4, 2, 2, 2, 2, 4, 2, 1, 2], // Ice
        [4, 2, 2, 2, 2, 4, 2, 1, 2, 1, 1, 1, 4, 0, 2, 4, 4, 1], // Fighting
        [2, 2, 2, 2, 4, 2, 2, 1, 1, 2, 2, 2, 1, 1, 2, 2, 0, 4], // Poison
        [2, 4, 2, 4, 1, 2, 2, 4, 2, 0, 2, 1, 4, 2, 2, 2, 4, 2], // Ground
        [2, 2, 2, 1, 4, 2, 4, 2, 2, 2, 2, 4, 1, 2, 2, 2, 1, 2], // Flying
        [2, 2, 2, 2, 2, 2, 4, 4, 2, 2, 1, 2, 2, 2, 2, 0, 1, 2], // Psychic
        [2, 1, 2, 2, 4, 2, 1, 1, 2, 1, 4, 2, 2, 1, 2, 4, 1, 1], // Bug
        [2, 4, 2, 2, 2, 4, 1, 2, 1, 4, 2, 4, 2, 2, 2, 2, 1, 2], // Rock
        [0, 2, 2, 2, 2, 2, 2, 2, 2, 2, 4, 2, 2, 4, 2, 1, 2, 2], // Ghost
        [2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 2, 4, 2, 1, 0], // Dragon
        [2, 2, 2, 2, 2, 2, 1, 2, 2, 2, 4, 2, 2, 4, 2, 1, 2, 1], // Dark
        [2, 1, 1, 1, 2, 4, 2, 2, 2, 2, 2, 2, 4, 2, 2, 2, 1, 4], // Steel
        [2, 1, 2, 2, 2, 2, 4, 1, 2, 2, 2, 2, 2, 2, 4, 4, 1, 2], // Fairy
    ];

    public static IReadOnlyList<string> Types { get; } = Order.Select(t => "TYPE_" + t).ToList();

    private static int Index(string type) => Array.IndexOf(Order, type.Replace("TYPE_", "", StringComparison.Ordinal));

    /// <summary>Damage multiplier of an attack type against a defender's types (0, 0.25 … 4).</summary>
    public static double Multiplier(string attack, IEnumerable<string> defender)
    {
        var a = Index(attack);
        var m = 1.0;
        if (a < 0)
        {
            return m;
        }

        foreach (var d in defender.Distinct())
        {
            var di = Index(d);
            if (di >= 0)
            {
                m *= Chart[a][di] / 2.0;
            }
        }

        return m;
    }

    /// <summary>Attack types that hit this type combination super effectively.</summary>
    public static IEnumerable<string> Weaknesses(IReadOnlyList<string> types) => Types.Where(t => Multiplier(t, types) > 1);

    public static IEnumerable<string> Resistances(IReadOnlyList<string> types) => Types.Where(t => Multiplier(t, types) < 1);
}

/// <summary>Natures that raise one stat and lower another (neutral ones change nothing).</summary>
public static class Natures
{
    // (nature, raised, lowered) with stats: 1 Atk, 2 Def, 3 SpA, 4 SpD, 5 Spe
    private static readonly (string Key, int Up, int Down)[] All =
    [
        ("NATURE_LONELY", 1, 2), ("NATURE_BRAVE", 1, 5), ("NATURE_ADAMANT", 1, 3), ("NATURE_NAUGHTY", 1, 4),
        ("NATURE_BOLD", 2, 1), ("NATURE_RELAXED", 2, 5), ("NATURE_IMPISH", 2, 3), ("NATURE_LAX", 2, 4),
        ("NATURE_TIMID", 5, 1), ("NATURE_HASTY", 5, 2), ("NATURE_JOLLY", 5, 3), ("NATURE_NAIVE", 5, 4),
        ("NATURE_MODEST", 3, 1), ("NATURE_MILD", 3, 2), ("NATURE_QUIET", 3, 5), ("NATURE_RASH", 3, 4),
        ("NATURE_CALM", 4, 1), ("NATURE_GENTLE", 4, 2), ("NATURE_SASSY", 4, 5), ("NATURE_CAREFUL", 4, 3),
    ];

    public static double Factor(string? nature, int stat)
    {
        var n = All.FirstOrDefault(x => x.Key == nature);
        return n.Key is null ? 1 : n.Up == stat ? 1.1 : n.Down == stat ? 0.9 : 1;
    }

    public static string For(int up, int down) => All.First(x => x.Up == up && x.Down == down).Key;
}

/// <summary>Stats at a level, and the main-series damage formula (Gen 5+), used for estimates.</summary>
public static class Calc
{
    public static int Hp(int baseHp, int level, int iv = 31, int ev = 0) =>
        baseHp == 1 ? 1 : ((2 * baseHp + iv + (ev / 4)) * level / 100) + level + 10;

    public static int Stat(int baseStat, int level, int iv, int ev, double nature) =>
        (int)Math.Floor((((2 * baseStat + iv + (ev / 4)) * level / 100) + 5) * nature);

    /// <summary>Final stats of a team member (IVs default to 31, EVs to 0, like the game).</summary>
    public static BaseStats StatsOf(Species s, TrainerPokemon p)
    {
        var iv = p.Ivs ?? new BaseStats(31, 31, 31, 31, 31, 31);
        var ev = p.Evs ?? new BaseStats(0, 0, 0, 0, 0, 0);
        int S(int b, int i, int e, int stat) => Stat(b, p.Level, i, e, Natures.Factor(p.Nature, stat));
        return new BaseStats(
            Hp(s.Stats.Hp, p.Level, iv.Hp, ev.Hp),
            S(s.Stats.Attack, iv.Attack, ev.Attack, 1),
            S(s.Stats.Defense, iv.Defense, ev.Defense, 2),
            S(s.Stats.SpAttack, iv.SpAttack, ev.SpAttack, 3),
            S(s.Stats.SpDefense, iv.SpDefense, ev.SpDefense, 4),
            S(s.Stats.Speed, iv.Speed, ev.Speed, 5));
    }

    /// <summary>Damage range (lowest, highest roll) of a move, ignoring crits, items, weather and abilities.</summary>
    public static (int Min, int Max) Damage(int level, int power, int attack, int defense, bool stab, double effectiveness)
    {
        if (power <= 0 || effectiveness == 0)
        {
            return (0, 0);
        }

        var baseDamage = Math.Floor(Math.Floor(Math.Floor(2.0 * level / 5 + 2) * power * attack / Math.Max(1, defense)) / 50) + 2;
        double Roll(double r) => Math.Floor(Math.Floor(baseDamage * r) * (stab ? 1.5 : 1) * effectiveness);
        return ((int)Roll(0.85), (int)Roll(1.0));
    }

    /// <summary>How many hits it takes at worst and at best (int.MaxValue when it can't hurt).</summary>
    public static (int Worst, int Best) HitsToKo((int Min, int Max) damage, int hp) =>
        damage.Max <= 0 ? (int.MaxValue, int.MaxValue) : ((int)Math.Ceiling(hp / (double)Math.Max(1, damage.Min)), (int)Math.Ceiling(hp / (double)damage.Max));
}
