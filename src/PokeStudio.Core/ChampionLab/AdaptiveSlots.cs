using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.ChampionLab;

/// <summary>
/// The adaptive boss slot: one member of a gym leader's, rival's or Elite Four member's team (never the Champion's) plus a few
/// candidates. With the new-game option on, the game picks whichever best counters the player's party at battle start.
/// </summary>
public static class AdaptiveSlots
{
    public const int DefaultCandidates = 3;

    /// <summary>Smaller teams keep their Pokémon (swapping one of two would change the whole fight).</summary>
    public const int MinTeam = 3;

    public static bool Allowed(TrainerGroup group) => group is TrainerGroup.EarlyGym or TrainerGroup.LateGym or TrainerGroup.Rival or TrainerGroup.EliteFour;

    /// <summary>The member that flexes by default: the one before the ace (the ace stays last), or the only one.</summary>
    public static int DefaultFlexIndex(Trainer t) => t.Party.Count >= 2 ? t.Party.Count - 2 : 0;

    /// <summary>
    /// The flexible member for this trainer: the one before the ace, but never the rival's starter (or its evolutions).
    /// -1 when no member may flex.
    /// </summary>
    public static int FlexIndexFor(GameData data, LabSettings lab, Trainer t)
    {
        if (t.Party.Count < MinTeam)
        {
            return -1;
        }

        var keep = new HashSet<string>(StringComparer.Ordinal);
        if (Lab.GroupOf(data, lab, t) == TrainerGroup.Rival)
        {
            foreach (var starter in data.Starters)
            {
                keep.UnionWith(Starters.Family(data, starter));
            }
        }

        for (var i = t.Party.Count - 2; i >= 0; i--)
        {
            if (!keep.Contains(t.Party[i].Species))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Candidates from the team builder (theme, legendary rules and level respected), different from the team and each other.</summary>
    public static List<TrainerPokemon> SuggestCandidates(GameData data, LabSettings lab, Trainer t, int flexIndex, int count = DefaultCandidates)
    {
        if (flexIndex < 0 || flexIndex >= t.Party.Count)
        {
            return [];
        }

        var flex = t.Party[flexIndex];
        int? Dex(string species) => data.Species.FirstOrDefault(s => s.Key == species)?.NationalDex;
        string Family(string species) => Dex(species)?.ToString(System.Globalization.CultureInfo.InvariantCulture) ?? species;
        string MainType(string species) => data.Species.FirstOrDefault(s => s.Key == species)?.Types.FirstOrDefault() ?? "";
        var taken = t.Party.Select(p => Family(p.Species)).ToHashSet(StringComparer.Ordinal);
        var types = new HashSet<string>(StringComparer.Ordinal) { MainType(flex.Species) };
        var result = new List<TrainerPokemon>();
        var difficulty = Lab.DifficultyOf(t);
        for (var variant = 0; variant < 8 && result.Count < count; variant++)
        {
            var team = TeamBuilder.Suggest(data, lab, t, new TeamBuilder.Options(difficulty, 6, flex.Level, variant));
            foreach (var member in team.Members.Select(m => m.Pokemon))
            {
                if (result.Count >= count)
                {
                    break;
                }

                // prefer new main types first; allow repeats on later passes so a narrow theme still gets candidates
                if (!taken.Contains(Family(member.Species)) && (variant >= 4 || types.Add(MainType(member.Species))))
                {
                    taken.Add(Family(member.Species));
                    result.Add(member with { Level = flex.Level });
                }
            }
        }

        return result;
    }

    public static IChange? Set(GameData data, Trainer t, int? flexIndex, IReadOnlyList<TrainerPokemon> candidates, string description)
    {
        var index = data.Trainers.FindIndex(x => x.Key == t.Key);
        if (index < 0)
        {
            return null;
        }

        var before = data.Trainers[index];
        var after = before with { FlexIndex = candidates.Count == 0 ? null : flexIndex, FlexCandidates = candidates.Count == 0 ? [] : candidates };
        return new ValueChange<Trainer>("Trainers", description, v => data.Trainers[index] = v, before, after);
    }

    /// <summary>Gives every gym leader, rival and Elite Four member without one an adaptive slot, as one undo step.</summary>
    public static (IChange? Change, int Count) SuggestForAll(GameData data, LabSettings lab)
    {
        var parts = new List<IChange>();
        foreach (var t in data.Trainers.ToList())
        {
            if (t.LockedReason is not null || t.HasAdaptiveSlot || !Allowed(Lab.GroupOf(data, lab, t)))
            {
                continue;
            }

            var flex = FlexIndexFor(data, lab, t);
            if (flex < 0)
            {
                continue;
            }

            var candidates = SuggestCandidates(data, lab, t, flex);
            if (candidates.Count > 0 && Set(data, t, flex, candidates, t.Name) is { } change)
            {
                parts.Add(change);
            }
        }

        return (parts.Count == 0 ? null : new CompositeChange("Champion Lab", $"Adaptive slots for {parts.Count} bosses", parts), parts.Count);
    }
}
