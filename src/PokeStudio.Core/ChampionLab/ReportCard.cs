using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Trainers;

namespace PokeStudio.Core.ChampionLab;

public sealed record MemberReport(string Name, int Level, int Speed, string BestMove, int ThreatsTwoHitKo, int ThreatsThatTwoHitKoIt);

/// <summary>How tough a boss is against what the player can have at that point.</summary>
public sealed record BossReport(
    int Score,
    string Verdict,
    IReadOnlyList<MemberReport> Members,
    IReadOnlyList<string> SharedWeaknesses,
    IReadOnlyList<string> Fixes,
    int ThreatCount,
    int Cap);

public static class ReportCard
{
    /// <summary>
    /// Every boss Pokémon is compared with every Pokémon the player can own by then (at the level cap, untrained), using the
    /// damage formula: who outspeeds, who knocks out whom in two hits. The score (1–10) blends level, coverage, bulk,
    /// training, items and the AI.
    /// </summary>
    public static BossReport For(GameData data, LabSettings lab, Trainer t)
    {
        var milestone = Lab.MilestoneOf(data, t);
        var caps = data.Rules?.LevelCaps ?? [];
        var cap = milestone >= 0 && milestone < caps.Count ? caps[milestone].Level : t.Party.Count > 0 ? t.Party.Max(p => p.Level) : 50;
        var scout = Scouting.Run(data);
        var threatKeys = milestone >= 0 && milestone < scout.Count ? scout[milestone].Species : data.Species.Where(s => s.IsEnabled).Select(s => s.Key).ToList();
        var threats = threatKeys.Select(k => data.Species.FirstOrDefault(s => s.Key == k)).Where(s => s is not null).Select(s => s!).ToList();
        var fixes = new List<string>();
        var members = new List<MemberReport>();
        var bossSpecies = new List<Species>();
        var c = data.Catalog;

        foreach (var p in t.Party)
        {
            if (data.Species.FirstOrDefault(s => s.Key == p.Species) is not { } s)
            {
                continue;
            }

            bossSpecies.Add(s);
            var stats = Calc.StatsOf(s, p);
            var moves = p.Moves.Select(m => data.Moves.FirstOrDefault(x => x.Key == m)).Where(m => m is { Power: > 0 }).Select(m => m!).ToList();
            if (moves.Count == 0)
            {
                // "learned automatically": assume a STAB move of 70 power
                moves = s.Types.Select(type => new Move { Id = -1, Key = "", Name = $"{c.NameOf(type)} attack", Type = type, Power = 70, Category = s.Stats.Attack >= s.Stats.SpAttack ? MoveCategory.Physical : MoveCategory.Special }).ToList();
            }

            var kos = 0;
            var koedBy = 0;
            var best = moves.MaxBy(m => m.Power)!;
            foreach (var th in threats)
            {
                var their = Calc.StatsOf(th, new TrainerPokemon { Species = th.Key, Level = cap });
                if (moves.Any(m => Calc.HitsToKo(Damage(p.Level, m, stats, their, s.Types, th.Types), their.Hp).Worst <= 2))
                {
                    kos++;
                }

                // the threat's best same-type attack (assumed 80 power, its better attacking side)
                var theirMoves = th.Types.Select(type => new Move { Id = -1, Key = "", Name = "", Type = type, Power = 80, Category = th.Stats.Attack >= th.Stats.SpAttack ? MoveCategory.Physical : MoveCategory.Special });
                if (theirMoves.Any(m => Calc.HitsToKo(Damage(cap, m, their, stats, th.Types, s.Types), stats.Hp).Worst <= 2))
                {
                    koedBy++;
                }
            }

            members.Add(new MemberReport(s.DisplayName, p.Level, stats.Speed, best.Name, kos, koedBy));
        }

        var weak = TypeChart.Types.Where(type => bossSpecies.Count(s => TypeChart.Multiplier(type, s.Types) > 1) >= Math.Max(2, (bossSpecies.Count + 1) / 2)).ToList();
        foreach (var w in weak.Take(3))
        {
            var resisters = TypeChart.Types.Where(x => TypeChart.Multiplier(w, [x]) < 1).Take(3).Select(c.NameOf);
            var list = string.Join(" or ", resisters);
            var article = list.Length > 0 && "AEIOU".Contains(char.ToUpperInvariant(list[0]), StringComparison.Ordinal) ? "an" : "a";
            fixes.Add($"{bossSpecies.Count(s => TypeChart.Multiplier(w, s.Types) > 1)} of {bossSpecies.Count} are weak to {c.NameOf(w)} — add {article} {list} type.");
        }

        var fastestThreat = threats.Select(th => (th, Speed: Calc.StatsOf(th, new TrainerPokemon { Species = th.Key, Level = cap }).Speed)).OrderByDescending(x => x.Speed).FirstOrDefault();
        if (fastestThreat.th is not null && members.Count > 0 && members.All(m => m.Speed < fastestThreat.Speed))
        {
            fixes.Add($"Nothing outspeeds {fastestThreat.th.DisplayName} (speed {fastestThreat.Speed}). A Choice Scarf or a faster Pokémon would help.");
        }

        if (members.Count > 0 && t.Party.Count < 6 && Lab.GroupOf(data, lab, t) is TrainerGroup.Champion or TrainerGroup.EliteFour)
        {
            fixes.Add($"Only {t.Party.Count} Pokémon — a full team of 6 is much harder.");
        }

        // score
        double score = 3;
        if (members.Count > 0)
        {
            var avgLevel = t.Party.Average(p => p.Level);
            score += Math.Clamp((avgLevel - cap) / 3.0, -2, 2);
            score += 2.5 * members.Average(m => m.ThreatsTwoHitKo / (double)Math.Max(1, threats.Count));
            score += 2.0 * (1 - members.Average(m => m.ThreatsThatTwoHitKoIt / (double)Math.Max(1, threats.Count)));
            score += t.Party.Count(p => p.HeldItem is { } i && i != "ITEM_NONE") * 0.15;
            score += t.Party.Count(p => p.Evs is not null) * 0.15;
            score += t.Party.Count / 6.0;
            score += AiBonus(data, t);
            score -= weak.Count * 0.4;
        }

        var final = (int)Math.Clamp(Math.Round(score), 1, 10);
        var verdict = final switch
        {
            <= 2 => "A gentle fight.",
            <= 4 => "A fair challenge.",
            <= 6 => "Tough — players will need a plan.",
            <= 8 => "Very hard — like a hard romhack's boss.",
            _ => "Brutal — Kaizo territory.",
        };
        return new BossReport(final, verdict, members, weak.Select(c.NameOf).ToList(), fixes, threats.Count, cap);
    }

    private static (int Min, int Max) Damage(int level, Move m, BaseStats attacker, BaseStats defender, IReadOnlyList<string> attackerTypes, IReadOnlyList<string> defenderTypes)
    {
        var physical = m.Category == MoveCategory.Physical;
        return Calc.Damage(level, m.Power, physical ? attacker.Attack : attacker.SpAttack, physical ? defender.Defense : defender.SpDefense,
            attackerTypes.Contains(m.Type), TypeChart.Multiplier(m.Type, defenderTypes));
    }

    private static double AiBonus(GameData data, Trainer t)
    {
        var preset = AiPresets.Match(t.AiFlags, AiPresets.For(data.Catalog))?.Id;
        return preset switch
        {
            "champion" or "double-ace" or "expert" => 1.5,
            "smart" => 1.0,
            "basic" => 0.4,
            _ => t.AiFlags.Count > 2 ? 0.8 : 0,
        };
    }
}

/// <summary>Creates Easy / Hard / Extra Hard versions of a trainer from its Normal team.</summary>
public static class DifficultyVersions
{
    public static string KeyFor(Trainer normal, Difficulty d) => $"{Lab.BaseKey(normal)}@{(d == Difficulty.ExtraHard ? "Extra Hard" : d.ToString())}";

    public static Trainer Make(GameData data, LabSettings lab, Trainer normal, Difficulty d)
    {
        var presets = AiPresets.For(data.Catalog);
        string[] Ai(params string[] ids) => (ids.Select(id => presets.FirstOrDefault(p => p.Id == id)).FirstOrDefault(p => p is not null)?.Flags ?? normal.AiFlags).ToArray();
        var group = Lab.GroupOf(data, lab, normal);
        var boss = group != TrainerGroup.Regular;
        switch (d)
        {
            case Difficulty.Easy:
                return normal with
                {
                    Key = KeyFor(normal, d),
                    Party = normal.Party.Select(p => p with { Level = Math.Max(1, p.Level - Math.Max(2, p.Level / 10)), Ivs = new BaseStats(0, 0, 0, 0, 0, 0), Evs = null, HeldItem = null }).ToList(),
                    Items = [],
                    AiFlags = Ai("beginner"),
                };
            case Difficulty.Hard:
            case Difficulty.ExtraHard:
            {
                var extra = d == Difficulty.ExtraHard;
                var size = extra && boss ? 6 : Math.Max(normal.Party.Count, boss ? Math.Min(6, normal.Party.Count + 1) : normal.Party.Count);
                var level = normal.Party.Count > 0 ? normal.Party.Max(p => p.Level) + (extra ? 3 : 1) : 20;
                var suggestion = TeamBuilder.Suggest(data, lab, normal, new TeamBuilder.Options(d, size, level));
                var party = suggestion.Members.Select(m => m.Pokemon).ToList();
                // never smaller than the original (a double battle needs two): fill from the original team, levelled up
                foreach (var p in normal.Party.Where(p => party.All(x => x.Species != p.Species)))
                {
                    if (party.Count >= Math.Max(normal.Party.Count, normal.Battle == BattleType.Double ? 2 : 1))
                    {
                        break;
                    }

                    party.Insert(0, p with { Level = Math.Min(100, p.Level + (extra ? 3 : 1)) });
                }

                return normal with
                {
                    Key = KeyFor(normal, d),
                    Party = party.Count > 0 ? party : normal.Party,
                    AiFlags = extra ? Ai(normal.Battle == BattleType.Double ? "double-ace" : "champion", "expert") : Ai("smart", "basic"),
                    Items = boss ? normal.Items : [],
                };
            }

            default:
                return normal;
        }
    }

    /// <summary>Adds or replaces the chosen versions for the given trainers, as one undo step.</summary>
    public static IChange? Generate(GameData data, LabSettings lab, IEnumerable<string> baseKeys, IReadOnlyList<Difficulty> versions)
    {
        var parts = new List<IChange>();
        foreach (var key in baseKeys)
        {
            var normal = data.Trainers.FirstOrDefault(t => t.Key == key && t.LockedReason is null && t.Party.Count > 0);
            if (normal is null)
            {
                continue;
            }

            foreach (var d in versions.Where(v => v != Difficulty.Normal))
            {
                var made = Make(data, lab, normal, d);
                var existing = data.Trainers.FindIndex(t => t.Key == made.Key);
                if (existing >= 0)
                {
                    var before = data.Trainers[existing];
                    parts.Add(new ValueChange<Trainer>("Trainers", made.Key, v => data.Trainers[data.Trainers.FindIndex(x => x.Key == made.Key)] = v, before, made with { Id = before.Id }));
                }
                else
                {
                    parts.Add(new ListInsertChange<Trainer>("Trainers", made.Key, data.Trainers, made with { Id = data.Trainers.Count }, data.Trainers.IndexOf(normal) + 1));
                }
            }
        }

        return parts.Count == 0 ? null : new CompositeChange("Trainers", $"Difficulty versions for {baseKeys.Count()} trainer(s): {string.Join(", ", versions.Where(v => v != Difficulty.Normal).Select(Lab.Label))}", parts);
    }
}

/// <summary>Inserts an item into a list; undo removes it.</summary>
public sealed class ListInsertChange<T>(string area, string description, List<T> list, T item, int index) : IChange
    where T : class
{
    public string Area { get; } = area;
    public string Description { get; } = description;

    public void Apply() => list.Insert(Math.Clamp(index, 0, list.Count), item);

    public void Revert() => list.Remove(item);
}
