using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.ChampionLab;

/// <summary>Rules for the moves trainers get: how strong a move may be at a level, and moves left out of the game.</summary>
public static class MoveRules
{
    /// <summary>
    /// The strongest attack a trainer's Pokémon gets at a level, so bosses hit about as hard as the player's team can:
    /// 50 power up to Lv 10, rising to no limit from Lv 61. Hard allows 10 more and Extra Hard 20 more. A move the
    /// Pokémon learns by levelling up by then is always fair (see <see cref="Allowed"/>).
    /// </summary>
    public static int MaxPower(int level, Difficulty difficulty)
    {
        var cap = level switch
        {
            <= 10 => 50,
            <= 15 => 60,
            <= 20 => 70,
            <= 25 => 75,
            <= 30 => 80,
            <= 35 => 90,
            <= 40 => 95,
            <= 50 => 110,
            <= 60 => 120,
            _ => 250,
        };
        return cap >= 250 ? cap : cap + difficulty switch { Difficulty.Hard => 10, Difficulty.ExtraHard => 20, _ => 0 };
    }

    /// <summary>
    /// Whether a trainer's Pokémon at <paramref name="level"/> may use <paramref name="move"/>: never a move left out of
    /// the game; any move it learns by levelling up by then; other attacks (TMs, tutors, egg moves) up to
    /// <see cref="MaxPower"/>; set-up and other status moves from Lv 25 (Lv 20 on Hard and up).
    /// </summary>
    public static bool Allowed(Move move, int level, Difficulty difficulty, IReadOnlySet<string> naturalMoves, IReadOnlyCollection<string> leftOut)
    {
        if (leftOut.Contains(move.Key))
        {
            return false;
        }

        if (naturalMoves.Contains(move.Key))
        {
            return true;
        }

        return move.Power > 0
            ? move.Power <= MaxPower(level, difficulty)
            : level >= (difficulty >= Difficulty.Hard ? 20 : 25);
    }

    /// <summary>
    /// Leaves a move out of the game (or lets the team builder use it again): as one undo step, the setting, and when
    /// leaving it out, the move taken out of every learnset (level-up, TM/tutor, egg) and every trainer's team. Letting
    /// it back in doesn't put it back in learnsets: Undo does that. Null when nothing changes.
    /// </summary>
    public static IChange? LeaveOut(GameData data, string moveKey, bool leaveOut)
    {
        var lab = data.Lab ?? new LabSettings();
        var parts = new List<IChange>();
        var list = lab.LeftOutMoves.Where(m => m != moveKey).ToList();
        if (leaveOut)
        {
            list.Add(moveKey);
        }

        if (!list.SequenceEqual(lab.LeftOutMoves))
        {
            parts.Add(new ValueChange<LabSettings?>("Champion Lab", "Moves left out", v => data.Lab = v, data.Lab, lab with { LeftOutMoves = list }));
        }

        if (leaveOut)
        {
            for (var i = 0; i < data.Learnsets.Count; i++)
            {
                var l = data.Learnsets[i];
                var after = l with
                {
                    LevelUp = l.LockedParts.ContainsKey("levelup") ? l.LevelUp : l.LevelUp.Where(m => m.Move != moveKey).ToList(),
                    Teachable = l.LockedParts.ContainsKey("teachable") ? l.Teachable : l.Teachable.Where(m => m != moveKey).ToList(),
                    EggMoves = l.LockedParts.ContainsKey("egg") ? l.EggMoves : l.EggMoves.Where(m => m != moveKey).ToList(),
                };
                if (after.LevelUp.Count != l.LevelUp.Count || after.Teachable.Count != l.Teachable.Count || after.EggMoves.Count != l.EggMoves.Count)
                {
                    var index = i;
                    parts.Add(new ValueChange<Learnset>("Learnsets", l.Key, v => data.Learnsets[index] = v, l, after));
                }
            }

            for (var i = 0; i < data.Trainers.Count; i++)
            {
                var t = data.Trainers[i];
                if (t.Party.Any(p => p.Moves.Contains(moveKey)))
                {
                    var index = i;
                    var after = t with { Party = t.Party.Select(p => p.Moves.Contains(moveKey) ? p with { Moves = p.Moves.Where(m => m != moveKey).ToList() } : p).ToList() };
                    parts.Add(new ValueChange<Trainer>("Trainers", t.Key, v => data.Trainers[index] = v, t, after));
                }
            }
        }

        var name = data.Moves.FirstOrDefault(m => m.Key == moveKey)?.Name ?? moveKey;
        return parts.Count == 0 ? null : new CompositeChange("Moves", leaveOut ? $"Leave {name} out of the game" : $"Let trainers use {name} again", parts);
    }
}
