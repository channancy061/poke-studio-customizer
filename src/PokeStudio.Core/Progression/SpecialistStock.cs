using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Progression;

/// <summary>
/// What the Mart Specialist sells: held items, special Poké Balls and TMs, growing with the badges (tier 0 = no badge,
/// tier 8 = all eight). Strong items come later so the early game stays fair, and a TM is sold once its move's power is
/// fair for the next boss's level (the same rule the bosses' moves follow). Only items the game has, with a price, are
/// listed (the game's own prices are used).
/// </summary>
public static class SpecialistStock
{
    private const string I = "ITEM_";

    /// <summary>The badge count from which each item is sold.</summary>
    public static IReadOnlyList<(int Badges, string Item)> Items { get; } =
    [
        // balls with a niche, from the start; the strong ones (Quick, Dusk, Timer) after the 3rd badge
        .. new[] { "NET_BALL", "NEST_BALL", "DIVE_BALL", "REPEAT_BALL", "LUXURY_BALL", "HEAL_BALL", "LEVEL_BALL", "LURE_BALL", "MOON_BALL", "FRIEND_BALL", "LOVE_BALL", "HEAVY_BALL", "FAST_BALL" }.Select(b => (0, I + b)),
        .. new[] { "QUICK_BALL", "DUSK_BALL", "TIMER_BALL" }.Select(b => (3, I + b)),

        // berries and type boosters early
        .. new[] { "ORAN_BERRY", "CHERI_BERRY", "CHESTO_BERRY", "PECHA_BERRY", "RAWST_BERRY", "ASPEAR_BERRY", "PERSIM_BERRY" }.Select(b => (0, I + b)),
        .. new[] { "SITRUS_BERRY", "CHARCOAL", "MYSTIC_WATER", "MIRACLE_SEED", "MAGNET", "NEVER_MELT_ICE", "BLACK_BELT", "POISON_BARB", "SOFT_SAND", "SHARP_BEAK", "TWISTED_SPOON", "SILVER_POWDER", "HARD_STONE", "SPELL_TAG", "DRAGON_FANG", "BLACK_GLASSES", "METAL_COAT", "SILK_SCARF", "FAIRY_FEATHER" }.Select(b => (1, I + b)),

        // everyday held items mid-game
        .. new[] { "QUICK_CLAW", "SCOPE_LENS", "SHELL_BELL", "KINGS_ROCK", "SMOOTH_ROCK", "DAMP_ROCK", "HEAT_ROCK", "ICY_ROCK" }.Select(b => (2, I + b)),
        .. new[] { "LUM_BERRY", "LEFTOVERS", "BLACK_SLUDGE", "MUSCLE_BAND", "WISE_GLASSES", "WHITE_HERB", "MENTAL_HERB", "EVIOLITE" }.Select(b => (3, I + b)),
        .. new[] { "ROCKY_HELMET", "EXPERT_BELT", "LIGHT_CLAY", "AIR_BALLOON", "POWER_HERB", "TOXIC_ORB", "FLAME_ORB", "BIG_ROOT", "BINDING_BAND" }.Select(b => (4, I + b)),

        // the strong competitive items later
        .. new[] { "FOCUS_SASH", "LIFE_ORB", "ASSAULT_VEST", "HEAVY_DUTY_BOOTS", "RED_CARD", "EJECT_BUTTON" }.Select(b => (5, I + b)),
        .. new[] { "CHOICE_BAND", "CHOICE_SPECS", "CHOICE_SCARF", "WEAKNESS_POLICY", "THROAT_SPRAY", "BLUNDER_POLICY" }.Select(b => (6, I + b)),
    ];

    /// <summary>The stock for 0…8 badges (each tier has everything of the ones before), the game's items only.</summary>
    public static List<List<string>> Tiers(GameData data)
    {
        var priced = data.Items.Where(i => i.Price > 0).Select(i => i.Key).ToHashSet(StringComparer.Ordinal);
        var caps = data.Rules?.LevelCaps ?? [];
        int CapFor(int badges)
        {
            // the next boss after this many badges: its badge's cap (after the 8th, the last cap)
            var next = caps.FirstOrDefault(c => c.Milestone == $"FLAG_BADGE0{Math.Min(badges + 1, 8)}_GET");
            return badges >= 8 ? caps.Select(c => c.Level).DefaultIfEmpty(100).Max() : next?.Level ?? (10 + (badges * 7));
        }

        var leftOut = data.Lab?.LeftOutMoves ?? [];
        var moves = data.Moves.ToDictionary(m => m.Key, StringComparer.Ordinal);
        var tms = data.Catalog.Machines.Where(m => m.Label.StartsWith("TM", StringComparison.Ordinal)).ToList();
        var tiers = new List<List<string>>();
        for (var badges = 0; badges <= 8; badges++)
        {
            var cap = CapFor(badges);
            var stock = Items.Where(x => x.Badges <= badges && priced.Contains(x.Item)).Select(x => x.Item).ToList();
            foreach (var (label, move) in tms)
            {
                var item = "ITEM_" + label;
                var tmItem = priced.Contains(item) ? item : "ITEM_TM_" + move.Replace("MOVE_", "", StringComparison.Ordinal);
                if (!priced.Contains(tmItem) || leftOut.Contains(move) || !moves.TryGetValue(move, out var m))
                {
                    continue;
                }

                // attacks once their power is fair for the next boss's level; status moves from Lv 20
                var fair = m.Power > 0 ? m.Power <= MoveRules.MaxPower(cap, Difficulty.Normal) : cap >= 20;
                if (fair)
                {
                    stock.Add(tmItem);
                }
            }

            tiers.Add(stock.Distinct(StringComparer.Ordinal).ToList());
        }

        return tiers;
    }
}
