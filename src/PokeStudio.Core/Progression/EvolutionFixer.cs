using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Progression;

/// <summary>How an evolution the player can't do (trade, time of day) is replaced.</summary>
public enum EvolutionFix
{
    /// <summary>Keep it as it is.</summary>
    Leave,

    /// <summary>Level up: at a level (trades) or at any time of day (time evolutions, other requirements kept).</summary>
    Level,

    /// <summary>Use an evolution item: Linking Cord for trades; Sun / Moon / Dusk Stone for day / night / evening.</summary>
    Stone,

    /// <summary>Level up while holding an item (the trade item, the Linking Cord, or the matching stone).</summary>
    HoldItem,
}

public enum EvolutionProblem
{
    Trade,
    TradeWithItem,
    TradePartner,
    TimeOfDay,

    /// <summary>The player has to spin on the spot (Alcremie, from Sword and Shield).</summary>
    Spin,
}

/// <summary>One evolution that needs a fix, with the fix the rules suggest.</summary>
public sealed record EvolutionFixRow(
    string FromSpecies,
    int Index,
    Evolution Original,
    EvolutionProblem Problem,
    EvolutionFix Suggested,
    IReadOnlyList<EvolutionFix> Choices,
    string Reason)
{
    /// <summary>
    /// For a spin evolution: the share of Pokémon (by the upper personality value, 0–9) that take this branch among the
    /// branches that would otherwise trigger the same way, so every form stays reachable.
    /// </summary>
    public (int From, int To)? Share { get; init; }
}

/// <summary>
/// Finds evolutions that need trading or a time of day and turns them into ones a single player can do. Rule-based:
/// trades → level (or Linking Cord), trades with an item → level up holding that item, time of day → any time
/// (or the matching stone when two branches would otherwise clash, e.g. Espeon and Umbreon).
/// </summary>
public static class EvolutionFixer
{
    public const int DefaultTradeLevel = 36;
    public const string LinkingCord = "ITEM_LINKING_CORD";

    private static readonly string[] TimeKinds = ["IF_TIME", "IF_NOT_TIME"];
    private static readonly string[] TradeKinds = ["IF_HOLD_ITEM", "IF_TRADE_PARTNER_SPECIES"];

    // only level-up and item evolutions: special ones (Alcremie spinning…) keep their own rules
    private static bool IsTime(Evolution e) => e.Method is "EVO_LEVEL" or "EVO_ITEM" && e.Conditions.Any(c => TimeKinds.Contains(c.Kind));

    /// <summary>"Day", "Night" or "Evening": the time of day a time evolution needs.</summary>
    public static string TimeOf(Evolution e)
    {
        var c = e.Conditions.First(c => TimeKinds.Contains(c.Kind));
        var night = c.Value.Contains("NIGHT", StringComparison.Ordinal);
        var evening = c.Value.Contains("EVENING", StringComparison.Ordinal);
        return c.Kind == "IF_NOT_TIME" ? (night ? "Day" : "Night") : night ? "Night" : evening ? "Evening" : "Day";
    }

    public static string StoneFor(string time) => time switch
    {
        "Night" => "ITEM_MOON_STONE",
        "Evening" => "ITEM_DUSK_STONE",
        _ => "ITEM_SUN_STONE",
    };

    /// <summary>Every trade / time-of-day evolution in the game that can be edited, with a suggested fix.</summary>
    public static List<EvolutionFixRow> Find(GameData data, bool trades = true, bool timeOfDay = true, bool spins = true)
    {
        var hasCord = data.Items.Any(i => i.Key == LinkingCord);
        var heldOk = data.HeldItemLevelEvolutions; // Gen 3 can't evolve by levelling up while holding an item
        bool HasItem(string key) => data.Items.Count == 0 || data.Items.Any(i => i.Key == key);
        var rows = new List<EvolutionFixRow>();
        foreach (var set in data.Evolutions.Where(s => s.LockedReason is null || s.EntriesReplaceable))
        {
            // relaxed versions of the time branches that are kept as "level up": a later one that would clash gets a stone
            var kept = set.Evolutions.Where(e => !IsTime(e) && e.Method != "EVO_TRADE").Select(Signature).ToHashSet(StringComparer.Ordinal);
            var timeOrder = set.Evolutions.Select((e, i) => (e, i)).Where(x => timeOfDay && IsTime(x.e))
                .OrderBy(x => TimeOf(x.e) == "Day" ? 0 : 1).ThenBy(x => x.i).Select(x => x.i).ToList();
            var timeSuggestion = new Dictionary<int, EvolutionFix>();
            foreach (var i in timeOrder)
            {
                var relaxed = Signature(Relax(set.Evolutions[i]));
                timeSuggestion[i] = kept.Add(relaxed) ? EvolutionFix.Level : EvolutionFix.Stone;
            }

            // spinning (Alcremie): level up holding the same Sweet at the same time of day; the branches that would then
            // trigger the same way share out by the Pokémon's personality, so every cream is still reachable
            if (spins && heldOk)
            {
                foreach (var group in set.Evolutions.Select((e, i) => (e, i)).Where(x => x.e.Method == "EVO_SPIN")
                             .GroupBy(x => string.Join("|", x.e.Conditions.Select(c => $"{c.Kind}={c.Value}"))))
                {
                    var branches = group.ToList();
                    for (var j = 0; j < branches.Count; j++)
                    {
                        var (e, i) = branches[j];
                        var item = e.Conditions.FirstOrDefault(c => c.Kind == "IF_HOLD_ITEM")?.Value;
                        var time = e.Conditions.Any(c => TimeKinds.Contains(c.Kind)) ? TimeOf(e).ToLowerInvariant() : null;
                        var reason = $"Needs the player to spin on the spot (Sword and Shield); it now evolves on level-up"
                            + (item is null ? "" : $" while holding {Name(data, item)}") + (time is null ? "" : $" in the {time}")
                            + (branches.Count > 1 ? $", for {Math.Max(1, 10 / branches.Count) * 10}% of them (by personality)." : ".");
                        rows.Add(new EvolutionFixRow(set.FromSpecies, i, e, EvolutionProblem.Spin, EvolutionFix.HoldItem, [EvolutionFix.Leave, EvolutionFix.HoldItem], reason)
                        {
                            Share = branches.Count > 1 ? (10 * j / branches.Count, 10 * (j + 1) / branches.Count) : null,
                        });
                    }
                }
            }

            for (var i = 0; i < set.Evolutions.Count; i++)
            {
                var e = set.Evolutions[i];
                if (trades && e.Method == "EVO_TRADE")
                {
                    var item = e.Conditions.FirstOrDefault(c => c.Kind == "IF_HOLD_ITEM")?.Value;
                    var partner = e.Conditions.Any(c => c.Kind == "IF_TRADE_PARTNER_SPECIES");
                    var problem = item is not null ? EvolutionProblem.TradeWithItem : partner ? EvolutionProblem.TradePartner : EvolutionProblem.Trade;
                    var choices = new List<EvolutionFix> { EvolutionFix.Leave, EvolutionFix.Level };
                    if (hasCord)
                    {
                        choices.Add(EvolutionFix.Stone);
                    }

                    if (heldOk && (item is not null || hasCord))
                    {
                        choices.Add(EvolutionFix.HoldItem);
                    }

                    var suggested = item is not null && heldOk ? EvolutionFix.HoldItem : EvolutionFix.Level;
                    var reason = problem switch
                    {
                        EvolutionProblem.TradeWithItem when heldOk => $"Needs a trade while holding {Name(data, item!)}; it now evolves on level-up while holding it.",
                        EvolutionProblem.TradeWithItem => $"Needs a trade while holding {Name(data, item!)}; it now evolves at a level (this game can't evolve by holding an item).",
                        EvolutionProblem.TradePartner => "Needs a trade for a particular Pokémon; it now evolves at a level.",
                        _ => "Needs a trade; it now evolves at a level.",
                    };
                    rows.Add(new EvolutionFixRow(set.FromSpecies, i, e, problem, suggested, choices, reason));
                }
                else if (timeSuggestion.TryGetValue(i, out var fix))
                {
                    var time = TimeOf(e);
                    var stone = HasItem(StoneFor(time));
                    // a game without "level up holding an item" can only keep the held item together with the time
                    var holdsItem = e.Conditions.Any(c => c.Kind == "IF_HOLD_ITEM");
                    var choices = new List<EvolutionFix> { EvolutionFix.Leave };
                    if (heldOk || !holdsItem)
                    {
                        choices.Add(EvolutionFix.Level);
                    }

                    if (stone)
                    {
                        choices.Add(EvolutionFix.Stone);
                    }

                    if (heldOk && stone && !e.Conditions.Any(c => c.Kind == "IF_HOLD_ITEM"))
                    {
                        choices.Add(EvolutionFix.HoldItem);
                    }

                    if (!heldOk && holdsItem)
                    {
                        fix = stone ? EvolutionFix.Stone : EvolutionFix.Leave;
                    }
                    else if (fix == EvolutionFix.Stone && !stone)
                    {
                        fix = EvolutionFix.Level;
                    }

                    var reason = fix == EvolutionFix.Leave
                        ? $"Only evolves in the {time.ToLowerInvariant()} while holding an item; this game has no other way to keep the held item, so it stays."
                        : fix == EvolutionFix.Level
                        ? $"Only evolves in the {time.ToLowerInvariant()}; it now evolves at any time."
                        : $"Only evolves in the {time.ToLowerInvariant()}, and at any time it would clash with another evolution; it now uses a {Name(data, StoneFor(time))}.";
                    rows.Add(new EvolutionFixRow(set.FromSpecies, i, e, EvolutionProblem.TimeOfDay, fix, choices, reason));
                }
            }
        }

        return rows;
    }

    /// <summary>The evolution after a fix.</summary>
    public static Evolution Apply(EvolutionFixRow row, EvolutionFix fix, int tradeLevel = DefaultTradeLevel)
    {
        var e = row.Original;
        if (fix == EvolutionFix.Leave)
        {
            return e;
        }

        if (row.Problem == EvolutionProblem.Spin)
        {
            // the Sweet and the time of day stay; the personality share picks among the branches that now look alike
            var conditions = e.Conditions.ToList();
            if (row.Share is { } share)
            {
                if (share.From > 0)
                {
                    conditions.Add(new EvolutionCondition("IF_PID_UPPER_MODULO_10_GT", (share.From - 1).ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }

                if (share.To < 10)
                {
                    conditions.Add(new EvolutionCondition("IF_PID_UPPER_MODULO_10_LT", share.To.ToString(System.Globalization.CultureInfo.InvariantCulture)));
                }
            }

            return new Evolution("EVO_LEVEL", "0", e.TargetSpecies) { Conditions = conditions };
        }

        if (row.Problem == EvolutionProblem.TimeOfDay)
        {
            var time = TimeOf(e);
            var others = e.Conditions.Where(c => !TimeKinds.Contains(c.Kind)).ToList();
            return fix switch
            {
                EvolutionFix.Level => Relax(e, tradeLevel),
                EvolutionFix.Stone => new Evolution("EVO_ITEM", StoneFor(time), e.TargetSpecies) { Conditions = Plain(others) },
                _ => new Evolution("EVO_LEVEL", e.Method == "EVO_LEVEL" ? e.Parameter : "0", e.TargetSpecies)
                {
                    Conditions = [.. others.Where(c => c.Kind != "IF_HOLD_ITEM"), new EvolutionCondition("IF_HOLD_ITEM", StoneFor(time))],
                },
            };
        }

        var kept = e.Conditions.Where(c => !TradeKinds.Contains(c.Kind)).ToList();
        var item = e.Conditions.FirstOrDefault(c => c.Kind == "IF_HOLD_ITEM")?.Value ?? LinkingCord;
        return fix switch
        {
            EvolutionFix.Level => new Evolution("EVO_LEVEL", tradeLevel.ToString(System.Globalization.CultureInfo.InvariantCulture), e.TargetSpecies) { Conditions = kept },
            EvolutionFix.Stone => new Evolution("EVO_ITEM", LinkingCord, e.TargetSpecies) { Conditions = kept },
            _ => new Evolution("EVO_LEVEL", "0", e.TargetSpecies) { Conditions = [.. kept, new EvolutionCondition("IF_HOLD_ITEM", item)] },
        };
    }

    /// <summary>
    /// One undo step that applies the chosen fixes. Returns the change (null when nothing changes) and warnings about
    /// branches of one Pokémon that would now trigger the same way.
    /// </summary>
    public static (IChange? Change, List<string> Warnings) Build(GameData data, IEnumerable<(EvolutionFixRow Row, EvolutionFix Fix)> choices, int tradeLevel = DefaultTradeLevel)
    {
        var parts = new List<IChange>();
        var warnings = new List<string>();
        foreach (var group in choices.Where(c => c.Fix != EvolutionFix.Leave).GroupBy(c => c.Row.FromSpecies))
        {
            var index = data.Evolutions.FindIndex(s => s.FromSpecies == group.Key);
            if (index < 0)
            {
                continue;
            }

            var before = data.Evolutions[index];
            var list = before.Evolutions.ToList();
            foreach (var (row, fix) in group)
            {
                if (row.Index < list.Count && list[row.Index] == row.Original)
                {
                    list[row.Index] = Apply(row, fix, tradeLevel);
                }
            }

            // the same trigger leading to two different Pokémon: only the first would ever happen
            foreach (var clash in list.GroupBy(Signature).Where(g => g.Select(e => e.TargetSpecies).Distinct().Count() > 1))
            {
                warnings.Add($"{Name(data, group.Key)}: {string.Join(" and ", clash.Select(e => Name(data, e.TargetSpecies)))} now evolve the same way, so only the first can happen.");
            }

            // exact duplicates (e.g. an item branch the game already had) are dropped
            // (a list split by game settings keeps its length: entries are only replaced)
            var unique = before.EntriesReplaceable ? list : list.DistinctBy(e => Signature(e) + "|" + e.TargetSpecies).ToList();
            var after = before with { Evolutions = unique };
            var i = index;
            parts.Add(new ValueChange<EvolutionSet>("Evolutions", group.Key, v => data.Evolutions[i] = v, before, after));
        }

        return (parts.Count == 0 ? null : new CompositeChange("Evolutions", $"Fixed {parts.Count} Pokémon's evolutions", parts), warnings);
    }

    /// <summary>A plain description of how an evolution happens.</summary>
    public static string Describe(GameData data, Evolution e)
    {
        var how = e.Method switch
        {
            "EVO_LEVEL" when e.Parameter is "0" or "" => "Level up",
            "EVO_LEVEL" => $"Level {e.Parameter}",
            "EVO_ITEM" => $"Use {Name(data, e.Parameter)}",
            "EVO_TRADE" => "Trade",
            _ => data.Catalog.NameOf(e.Method),
        };
        var extra = e.Conditions.Select(c => c.Kind switch
        {
            "IF_HOLD_ITEM" => $"holding {Name(data, c.Value)}",
            "IF_TIME" or "IF_NOT_TIME" => $"in the {TimeOf(new Evolution("", "", "") { Conditions = [c] }).ToLowerInvariant()}",
            "IF_MIN_FRIENDSHIP" => "with high friendship",
            "IF_TRADE_PARTNER_SPECIES" => $"for {Name(data, c.Value)}",
            "IF_REGION" => "in " + GameCatalog.Prettify(c.Value.Replace("REGION_", "", StringComparison.Ordinal)),
            "IF_NOT_REGION" => "outside " + GameCatalog.Prettify(c.Value.Replace("REGION_", "", StringComparison.Ordinal)),
            "IF_GENDER" => c.Value.Contains("FEMALE", StringComparison.Ordinal) ? "if female" : "if male",
            "IF_KNOWS_MOVE" => $"knowing {data.Moves.FirstOrDefault(m => m.Key == c.Value)?.Name ?? c.Value}",
            _ => data.Catalog.NameOf(c.Kind) + " " + c.Value,
        });
        return string.Join(" ", new[] { how }.Concat(extra));
    }

    private static string Name(GameData data, string key) =>
        data.Items.FirstOrDefault(i => i.Key == key)?.Name
        ?? data.Species.FirstOrDefault(s => s.Key == key)?.Name
        ?? GameCatalog.Prettify(key.Replace("ITEM_", "", StringComparison.Ordinal).Replace("SPECIES_", "", StringComparison.Ordinal));

    /// <summary>Time conditions removed; a bare "level up" with nothing left would evolve at once, so it gets the trade level.</summary>
    private static Evolution Relax(Evolution e) => Relax(e, DefaultTradeLevel);

    private static Evolution Relax(Evolution e, int fallbackLevel)
    {
        var others = e.Conditions.Where(c => !TimeKinds.Contains(c.Kind)).ToList();
        var parameter = e.Method == "EVO_LEVEL" && e.Parameter == "0" && others.Count == 0
            ? fallbackLevel.ToString(System.Globalization.CultureInfo.InvariantCulture)
            : e.Parameter;
        return e with { Parameter = parameter, Conditions = others };
    }

    /// <summary>A stone evolution keeps only conditions that still make sense for an item (gender, region…).</summary>
    private static List<EvolutionCondition> Plain(IEnumerable<EvolutionCondition> conditions) =>
        conditions.Where(c => c.Kind is not ("IF_MIN_FRIENDSHIP" or "IF_HOLD_ITEM")).ToList();

    private static string Signature(Evolution e) =>
        $"{e.Method}|{e.Parameter}|{string.Join(";", e.Conditions.Select(c => c.Kind + "=" + c.Value).Order(StringComparer.Ordinal))}";
}
