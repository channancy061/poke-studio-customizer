using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Trainers;

namespace PokeStudio.Core.Progression;

/// <summary>
/// Ordinary trainers (Youngsters, Bird Keepers, Hikers…) to match the wild Pokémon: each team member becomes a Pokémon
/// the player can meet in the wild up to that point of the playthrough, sharing a type with the one it replaces (a Bird
/// Keeper keeps birds), at its level and evolved as far as that level allows. Moves are level-up moves only (no TMs),
/// attacks first, never the accuracy-lowering, evasion-raising or one-hit-KO moves that make an early fight luck
/// (a Pidgey spamming Sand Attack). With the type spread on (<see cref="LabSettings.RouteTypeSpread"/>) only themed
/// classes keep their type, and no type takes over a stretch of the game. Bosses and story battles are left alone.
/// </summary>
public static class RouteTrainers
{
    private static readonly string[] Unfair = ["ACCURACY_DOWN", "EVASION_UP", "MINIMIZE", "OHKO", "DOUBLE_TEAM"];

    /// <param name="places">Where each trainer stands ("Route 3"), from the game module; with a playthrough order it
    /// decides what the player could have met by then (otherwise the trainer's level does).</param>
    public static (IChange? Change, int Count) Refresh(GameData data, LabSettings lab, IReadOnlyDictionary<string, string>? places = null) =>
        Build(data, lab, places, extraHard: false);

    /// <summary>
    /// Nightmare (Extra Hard in the game's data) versions of every ordinary trainer: six Pokémon one level up, picked the
    /// same way (wild Pokémon met by then), made harder by <see cref="LabSettings.Nightmare"/>'s switches.
    /// </summary>
    public static (IChange? Change, int Count) ExtraHardTeams(GameData data, LabSettings lab, IReadOnlyDictionary<string, string>? places = null) =>
        Build(data, lab, places, extraHard: true);

    private static (IChange? Change, int Count) Build(GameData data, LabSettings lab, IReadOnlyDictionary<string, string>? places, bool extraHard)
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
        // evolved as far as the level allows; fully from the "fully evolved" level
        string FormAt(string root, int level) =>
            forms.TryGetValue((root, level), out var f) ? f
            : forms[(root, level)] = lab.FullyEvolvedFrom > 0 && level >= lab.FullyEvolvedFrom ? EvolutionPaths.EvolvedForm(data, root, 100, 0) : EvolutionPaths.EvolvedForm(data, root, level, 36);

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

        // the type spread: how often each type was picked between two level caps (themed classes keep their type)
        var used = new Dictionary<(int, string), int>();
        var picks = new Dictionary<int, int>();
        bool Crowded(int stretch, string k)
        {
            var room = Math.Max(2, (picks.GetValueOrDefault(stretch) + 1) * lab.RouteTypeShare / 100.0);
            return species[k].Types.Any(ty => used.GetValueOrDefault((stretch, ty)) + 1 > room);
        }

        static List<string> Prefer(List<string> list, Func<string, bool> good) => list.Where(good).ToList() is { Count: > 0 } better ? better : list;

        // Nightmare (the hardest difficulty, Extra Hard inside the game's data)
        var nm = extraHard ? lab.Nightmare : null;
        var threats = new Dictionary<int, IReadOnlyList<string>>();
        var presets = AiPresets.For(data.Catalog);
        var expertAi = presets.FirstOrDefault(p => p.Id == "expert")?.Flags ?? presets.FirstOrDefault(p => p.Id == "champion")?.Flags;

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
            var boost = extraHard ? 1 : 0;
            var capLevel = caps[Math.Min(milestone, caps.Count - 1)].Level;
            var usedItems = new HashSet<string>(StringComparer.Ordinal);
            var teamTypes = t.Party.SelectMany(p => species.TryGetValue(p.Species, out var x) ? x.Types : []).ToHashSet(StringComparer.Ordinal);
            var top = t.Party.Max(p => p.Level);
            var themed = !lab.RouteTypeSpread || Defaults.IsThemed(lab.ThemedClasses, t.TrainerClass);
            for (var i = 0; i < (extraHard ? 6 : t.Party.Count); i++)
            {
                // Extra Hard: the team's own members one level up, then new ones at the top level until there are six
                var old = i < t.Party.Count ? t.Party[i] with { Level = Math.Min(100, t.Party[i].Level + boost) }
                    : t.Party[^1] with { Level = Math.Min(100, top + boost), HeldItem = null };
                if (nm?.AtLevelCap == true && capLevel > old.Level)
                {
                    old = old with { Level = Math.Min(100, capLevel) };
                }

                var oldTypes = i < t.Party.Count && species.TryGetValue(old.Species, out var known) ? known.Types : (IReadOnlyList<string>)[.. teamTypes];
                species.TryGetValue(old.Species, out var o);
                var limit = TeamBuilder.MaxStatTotal(old.Level, nm?.StrongerPokemon == true ? Difficulty.Hard : Difficulty.Easy);
                var options = available
                    .Select(r => FormAt(r, old.Level))
                    .Where(k => species.TryGetValue(k, out var s) && s.Stats.Total <= Math.Max(limit, o?.Stats.Total ?? 0) && party.All(p => p.Species != k))
                    .Distinct(StringComparer.Ordinal)
                    .ToList();
                var sameType = options.Where(k => species[k].Types.Any(oldTypes.Contains)).ToList();
                var pool = sameType.Count > 0 ? sameType : options;
                if (lab.RouteTypeSpread)
                {
                    // a themed class keeps its type, but not always the same Pokémon of it; anyone else gets any type the
                    // stretch hasn't had too much of, and one not on the team yet
                    var onTeam = party.SelectMany(p => species.TryGetValue(p.Species, out var x) ? x.Types : []).ToHashSet(StringComparer.Ordinal);
                    pool = themed
                        ? Prefer(pool, k => !Crowded(milestone, k))
                        : Prefer(Prefer(options, k => !Crowded(milestone, k)), k => !species[k].Types.Any(onTeam.Contains));
                }
                if (pool.Count == 0)
                {
                    party.Add(old with { Moves = FairMoves(data, old.Species, old.Level) ?? old.Moves });
                    continue;
                }

                // Nightmare: members that resist what the team so far is weak to come first
                if (nm?.CoverWeaknesses == true && party.Count > 0)
                {
                    var weak = TypeChart.Types.Where(a => party.Count(p => species.TryGetValue(p.Species, out var x) && TypeChart.Multiplier(a, x.Types) > 1) * 2 >= party.Count).ToList();
                    pool = Prefer(pool, k => weak.Count == 0 || weak.Any(a => TypeChart.Multiplier(a, species[k].Types) < 1));
                }

                // the same trainer always gets the same team; different trainers different ones
                var pick = pool.OrderBy(k => StableHash($"{t.Key}/{i}/{k}")).First();
                picks[milestone] = picks.GetValueOrDefault(milestone) + 1;
                foreach (var ty in species[pick].Types.Distinct(StringComparer.Ordinal))
                {
                    used[(milestone, ty)] = used.GetValueOrDefault((milestone, ty)) + 1;
                }

                var member = old with { Species = pick, Moves = FairMoves(data, pick, old.Level) ?? [], Ability = null, Nickname = null, Gender = null };
                if (nm?.FullTraining == true)
                {
                    if (!threats.TryGetValue(milestone, out var th))
                    {
                        threats[milestone] = th = TeamBuilder.ThreatTypes(data, milestone);
                    }

                    var trained = TeamBuilder.Trained(data, species[pick], old.Level, th, [.. party.Select(p => species[p.Species])], usedItems, lab.FullyEvolvedFrom);
                    member = member with
                    {
                        Moves = trained.Moves.Count > 0 ? trained.Moves : member.Moves,
                        Nature = trained.Nature, Ability = trained.Ability, Ivs = trained.Ivs, Evs = trained.Evs, HeldItem = trained.HeldItem,
                    };
                }

                member = nm is null ? member : member with { HeldItem = nm.HeldItems ? member.HeldItem : null };
                party.Add(member);
            }

            var key = t.Key;
            if (!extraHard)
            {
                parts.Add(new ValueChange<Trainer>("Trainers", t.Name, v => data.Trainers[data.Trainers.FindIndex(x => x.Key == key)] = v, t, t with { Party = party }));
                continue;
            }

            // the version sits next to the trainer (replaced when it's made again)
            var version = t with { Key = DifficultyVersions.KeyFor(t, Difficulty.ExtraHard), Party = party, AiFlags = nm?.SmartAi == true && expertAi is not null ? [.. expertAi] : t.AiFlags };
            var existing = data.Trainers.FindIndex(x => x.Key == version.Key);
            if (existing >= 0)
            {
                var before = data.Trainers[existing];
                parts.Add(new ValueChange<Trainer>("Trainers", version.Key, v => data.Trainers[data.Trainers.FindIndex(x => x.Key == version.Key)] = v, before, version with { Id = before.Id }));
            }
            else
            {
                parts.Add(new ListInsertChange<Trainer>("Trainers", version.Key, data.Trainers, version with { Id = data.Trainers.Count }, data.Trainers.IndexOf(t) + 1));
            }
        }

        return (parts.Count == 0 ? null : new CompositeChange("Trainers", extraHard
            ? $"Extra Hard teams of six for {parts.Count} route trainers"
            : $"Route trainers use the planned wild Pokémon ({parts.Count})", parts), parts.Count);
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
        var leftOut = data.Lab?.LeftOutMoves ?? [];
        var moves = data.Moves.Where(m => known.Contains(m.Key) && !leftOut.Contains(m.Key) && !Unfair.Any(u => m.Effect.Contains(u, StringComparison.Ordinal))).ToList();
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
