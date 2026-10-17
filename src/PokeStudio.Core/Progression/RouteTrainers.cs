using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Progression;

/// <summary>
/// Ordinary trainers (Youngsters, Bird Keepers, Hikers…) to match the wild Pokémon: each team member becomes a Pokémon
/// the player can meet in the wild up to that point of the playthrough, sharing a type with the one it replaces (a Bird
/// Keeper keeps birds), at its level and evolved as far as that level allows. Moves are level-up moves only (no TMs),
/// attacks first, never the accuracy-lowering, evasion-raising or one-hit-KO moves that make an early fight luck
/// (a Pidgey spamming Sand Attack). Bosses and story battles are left alone.
/// </summary>
public static class RouteTrainers
{
    private static readonly string[] Unfair = ["ACCURACY_DOWN", "EVASION_UP", "MINIMIZE", "OHKO", "DOUBLE_TEAM"];

    /// <param name="places">Where each trainer stands ("Route 3"), from the game module; with a playthrough order it
    /// decides what the player could have met by then (otherwise the trainer's level does).</param>
    public static (IChange? Change, int Count) Refresh(GameData data, LabSettings lab, IReadOnlyDictionary<string, string>? places = null)
    {
        var caps = data.Rules?.LevelCaps ?? [];
        var met = EncounterPlanner.Met(data);
        if (caps.Count == 0 || met.Count == 0)
        {
            return (null, 0);
        }

        var species = data.Species.ToDictionary(s => s.Key, StringComparer.Ordinal);
        var roots = met.Select(m => (Root: TeamBuilder.FamilyRoot(data, m.Species), m.From))
            .GroupBy(x => x.Root).Select(g => (Root: g.Key, From: g.Min(x => x.From))).ToList();
        var forms = new Dictionary<(string, int), string>();
        string FormAt(string root, int level) =>
            forms.TryGetValue((root, level), out var f) ? f : forms[(root, level)] = EvolutionPaths.EvolvedForm(data, root, level, 36);

        // a trainer's place in the playthrough: the area of the same name (a city's trainers count with its own area)
        var order = StoryOrder.For(data);
        var capPositions = StoryOrder.CapPositions(data);
        var areaByName = order is null ? [] : order.Where(x => x.StartsWith("MAP_", StringComparison.Ordinal))
            .GroupBy(StoryOrder.AreaName, StringComparer.OrdinalIgnoreCase).ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);
        int? ByPlace(Trainer t)
        {
            if (order is null || capPositions is null || places is null || !places.TryGetValue(Lab.BaseKey(t), out var place))
            {
                return null;
            }

            var area = areaByName.Where(a => place.Equals(a.Key, StringComparison.OrdinalIgnoreCase) || place.StartsWith(a.Key + " ", StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(a => a.Key.Length).Select(a => a.Value).FirstOrDefault();
            return area is null ? null : StoryOrder.NextBattle(capPositions, StoryOrder.AreaPosition(order, area)!.Value);
        }

        var parts = new List<IChange>();
        foreach (var t in data.Trainers.ToList())
        {
            if (t.Party.Count == 0 || t.LockedReason is not null || t.NeverFought || t.Key.Contains('@', StringComparison.Ordinal)
                || t.Key.StartsWith(Story.StoryBattles.TrainerPrefix, StringComparison.Ordinal) || Lab.GroupOf(data, lab, t) != TrainerGroup.Regular)
            {
                continue;
            }

            // what the player could have met by the time they reach this trainer
            var milestone = ByPlace(t) ?? Math.Max(0, Lab.MilestoneOf(data, t));
            var available = roots.Where(r => r.From <= milestone).Select(r => r.Root).ToList();
            if (available.Count == 0)
            {
                continue;
            }

            var party = new List<TrainerPokemon>();
            for (var i = 0; i < t.Party.Count; i++)
            {
                var old = t.Party[i];
                var oldTypes = species.TryGetValue(old.Species, out var o) ? o.Types : [];
                var limit = TeamBuilder.MaxStatTotal(old.Level, Difficulty.Easy);
                var options = available
                    .Select(r => FormAt(r, old.Level))
                    .Where(k => species.TryGetValue(k, out var s) && s.Stats.Total <= Math.Max(limit, o?.Stats.Total ?? 0) && party.All(p => p.Species != k))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var sameType = options.Where(k => species[k].Types.Any(oldTypes.Contains)).ToList();
                var pool = sameType.Count > 0 ? sameType : options;
                if (pool.Count == 0)
                {
                    party.Add(old with { Moves = FairMoves(data, old.Species, old.Level) ?? old.Moves });
                    continue;
                }

                // the same trainer always gets the same team; different trainers different ones
                var pick = pool.OrderBy(k => StableHash($"{t.Key}/{i}/{k}")).First();
                party.Add(old with { Species = pick, Moves = FairMoves(data, pick, old.Level) ?? [], Ability = null, Nickname = null, Gender = null });
            }

            var key = t.Key;
            parts.Add(new ValueChange<Trainer>("Trainers", t.Name, v => data.Trainers[data.Trainers.FindIndex(x => x.Key == key)] = v, t, t with { Party = party }));
        }

        return (parts.Count == 0 ? null : new CompositeChange("Trainers", $"Route trainers use the planned wild Pokémon ({parts.Count})", parts), parts.Count);
    }

    /// <summary>
    /// Up to four level-up moves the Pokémon knows by this level: its best attack of each of its types, then other attacks,
    /// then one fair status move; never accuracy-lowering, evasion or one-hit-KO moves. Null when the game has no learnsets.
    /// </summary>
    public static IReadOnlyList<string>? FairMoves(GameData data, string speciesKey, int level)
    {
        var s = data.Species.FirstOrDefault(x => x.Key == speciesKey);
        if (s is null || data.LearnsetFor(s) is not { } learnset || data.Moves.Count == 0)
        {
            return null;
        }

        var known = learnset.LevelUp.Where(m => m.Level <= level).Select(m => m.Move).ToHashSet(StringComparer.Ordinal);
        var moves = data.Moves.Where(m => known.Contains(m.Key) && !Unfair.Any(u => m.Effect.Contains(u, StringComparison.Ordinal))).ToList();
        double Value(Move m) => m.Power * (m.Accuracy == 0 ? 1 : m.Accuracy / 100.0) * (s.Types.Contains(m.Type) ? 1.5 : 1);
        var attacks = moves.Where(m => m.Category != MoveCategory.Status && m.Power > 0).ToList();
        var chosen = new List<Move>();
        foreach (var type in s.Types)
        {
            if (attacks.Where(m => m.Type == type).OrderByDescending(Value).FirstOrDefault() is { } stab && !chosen.Contains(stab))
            {
                chosen.Add(stab);
            }
        }

        chosen.AddRange(attacks.Except(chosen).OrderByDescending(Value).Take(3 - Math.Min(3, chosen.Count)));
        if (moves.Where(m => m.Category == MoveCategory.Status).OrderByDescending(m => m.Key, StringComparer.Ordinal).FirstOrDefault() is { } status && chosen.Count < 4)
        {
            chosen.Add(status);
        }

        return chosen.Count == 0 ? null : chosen.Take(4).Select(m => m.Key).ToList();
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
