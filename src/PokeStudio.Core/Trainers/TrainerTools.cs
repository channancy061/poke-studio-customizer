using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Trainers;

/// <summary>A named enemy AI setup made of the game's AI flags.</summary>
public sealed record AiPreset(string Id, string Name, string Description, IReadOnlyList<string> Flags);

public static class AiPresets
{
    /// <summary>Presets whose flags all exist in this game (pokeemerald-expansion's AI_FLAG_* names).</summary>
    public static IReadOnlyList<AiPreset> For(GameCatalog catalog)
    {
        var known = catalog.AiFlags.Select(f => f.Key).ToHashSet(StringComparer.Ordinal);
        AiPreset[] all =
        [
            new("none", "Random", "Picks moves at random, like the earliest games' weakest trainers.", []),
            new("beginner", "Beginner", "Avoids moves that would obviously fail, otherwise random. Most route trainers in the original games.",
                ["AI_FLAG_CHECK_BAD_MOVE"]),
            new("basic", "Basic", "Avoids useless moves, goes for knock-outs and picks sensible moves. Gym Leaders in the original games.",
                ["AI_FLAG_BASIC_TRAINER"]),
            new("smart", "Smart", "Knows your team's moves, switches out of bad match-ups and saves its strongest Pokémon well.",
                ["AI_FLAG_SMART_TRAINER"]),
            new("expert", "Expert", "Everything Smart does, plus predicts your switches and moves and plays around your HP. Very hard.",
                ["AI_FLAG_SMART_TRAINER", "AI_FLAG_PREDICTION", "AI_FLAG_HP_AWARE"]),
            new("champion", "Champion", "Expert, and saves its strongest Pokémon for last like a real champion.",
                ["AI_FLAG_SMART_TRAINER", "AI_FLAG_PREDICTION", "AI_FLAG_HP_AWARE", "AI_FLAG_ACE_POKEMON"]),
            new("double-ace", "Double Ace", "Expert, and keeps its two strongest Pokémon for the end — made for double battles.",
                ["AI_FLAG_SMART_TRAINER", "AI_FLAG_PREDICTION", "AI_FLAG_HP_AWARE", "AI_FLAG_DOUBLE_ACE_POKEMON"]),
        ];
        return all.Where(p => p.Flags.All(known.Contains)).ToList();
    }

    /// <summary>The preset these flags match exactly, or null ("Custom").</summary>
    public static AiPreset? Match(IReadOnlyList<string> flags, IReadOnlyList<AiPreset> presets) =>
        presets.FirstOrDefault(p => p.Flags.Count == flags.Count && p.Flags.ToHashSet(StringComparer.Ordinal).SetEquals(flags));
}

public enum LevelChangeMode
{
    /// <summary>Add (or subtract) a number of levels.</summary>
    AddLevels,

    /// <summary>Multiply by a percentage, e.g. 120 = +20%.</summary>
    Percent,
}

public static class TrainerTools
{
    public static int NewLevel(int level, LevelChangeMode mode, int amount, int maxLevel) =>
        Math.Clamp(mode == LevelChangeMode.AddLevels ? level + amount : (int)Math.Round(level * amount / 100.0, MidpointRounding.AwayFromZero), 1, maxLevel);

    /// <summary>Changes every team member's level for the given trainers, as one undo step.</summary>
    public static IChange? ChangeLevels(GameData data, IEnumerable<string> trainerKeys, LevelChangeMode mode, int amount, GameLimits limits)
    {
        var parts = new List<IChange>();
        var keys = trainerKeys.ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < data.Trainers.Count; i++)
        {
            var t = data.Trainers[i];
            if (!keys.Contains(t.Key) || t.LockedReason is not null)
            {
                continue;
            }

            var party = t.Party.Select(p => p with { Level = NewLevel(p.Level, mode, amount, limits.MaxLevel) }).ToList();
            if (party.Zip(t.Party).All(x => x.First.Level == x.Second.Level))
            {
                continue;
            }

            var index = i;
            parts.Add(new ValueChange<Trainer>("Trainers", t.Name, v => data.Trainers[index] = v, t, t with { Party = party }));
        }

        if (parts.Count == 0)
        {
            return null;
        }

        var how = mode == LevelChangeMode.AddLevels ? $"{amount:+0;-0} levels" : $"levels × {amount}%";
        return new CompositeChange("Trainers", parts.Count == 1 ? $"{((ValueChange<Trainer>)parts[0]).OldValue.Name} · {how}" : $"{parts.Count} trainers · {how}", parts);
    }

    /// <summary>Gives the trainers an AI setup, as one undo step.</summary>
    public static IChange? SetAi(GameData data, IEnumerable<string> trainerKeys, IReadOnlyList<string> flags, string label)
    {
        var parts = new List<IChange>();
        var keys = trainerKeys.ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < data.Trainers.Count; i++)
        {
            var t = data.Trainers[i];
            if (!keys.Contains(t.Key) || t.LockedReason is not null || t.AiFlags.SequenceEqual(flags))
            {
                continue;
            }

            var index = i;
            parts.Add(new ValueChange<Trainer>("Enemy AI", t.Name, v => data.Trainers[index] = v, t, t with { AiFlags = flags.ToList() }));
        }

        return parts.Count == 0 ? null
            : new CompositeChange("Enemy AI", parts.Count == 1 ? $"{((ValueChange<Trainer>)parts[0]).OldValue.Name} · AI set to {label}" : $"{parts.Count} trainers · AI set to {label}", parts);
    }
}
