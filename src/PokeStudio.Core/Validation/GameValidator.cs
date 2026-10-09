using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Validation;

public enum IssueSeverity
{
    /// <summary>Must be fixed before saving: the game would not build or would misbehave.</summary>
    Error,

    /// <summary>Worth a look; saving is allowed.</summary>
    Warning,
}

/// <summary>A problem explained in plain words, optionally with a one-click fix (applied through undo).</summary>
public sealed record ValidationIssue(IssueSeverity Severity, string Area, string Subject, string Message, string? Suggestion = null)
{
    /// <summary>Creates the change that fixes the problem; null when there is no automatic fix.</summary>
    public Func<GameData, IChange>? Fix { get; init; }

    /// <summary>Key of the species/learnset the issue belongs to, so the UI can jump there.</summary>
    public string? TargetKey { get; init; }
}

/// <summary>Checks every module gets for free. Modules add their own via <see cref="IGameModule.Validate"/>.</summary>
public static class GameValidator
{
    public static IReadOnlyList<ValidationIssue> Run(GameData data, IGameModule module)
    {
        var issues = new List<ValidationIssue>();
        var limits = module.Limits;
        var speciesKeys = new HashSet<string>(data.Species.Select(s => s.Key), StringComparer.Ordinal);
        var moveKeys = new HashSet<string>(data.Catalog.Moves.Select(m => m.Key), StringComparer.Ordinal);

        for (var i = 0; i < data.Species.Count; i++)
        {
            CheckSpecies(data, i, limits, issues);
        }

        for (var i = 0; i < data.Learnsets.Count; i++)
        {
            CheckLearnset(data, i, limits, moveKeys, issues);
        }

        foreach (var set in data.Evolutions.Where(e => e.LockedReason is null))
        {
            CheckEvolutions(data, set, limits, speciesKeys, issues);
        }

        issues.AddRange(module.Validate(data));
        return issues;
    }

    private static void CheckSpecies(GameData data, int index, GameLimits limits, List<ValidationIssue> issues)
    {
        var s = data.Species[index];
        if (!s.IsEnabled)
        {
            return;
        }

        var who = s.DisplayName;
        void Add(IssueSeverity sev, string message, string? suggestion = null, Func<GameData, IChange>? fix = null) =>
            issues.Add(new ValidationIssue(sev, "Pokémon", who, message, suggestion) { Fix = fix, TargetKey = s.Key });

        if (string.IsNullOrWhiteSpace(s.Name))
        {
            Add(IssueSeverity.Error, "has no name.", "Type a name.");
        }
        else if (s.Name.Length > limits.SpeciesNameLength)
        {
            Add(IssueSeverity.Error,
                $"name “{s.Name}” is {s.Name.Length} letters; this game allows {limits.SpeciesNameLength}.",
                $"Shorten it to “{s.Name[..limits.SpeciesNameLength]}”?",
                d => SpeciesChange(d, s.Key, "Name", x => x with { Name = x.Name[..limits.SpeciesNameLength] }));
        }

        var stats = new[] { ("HP", s.Stats.Hp), ("Attack", s.Stats.Attack), ("Defense", s.Stats.Defense), ("Sp. Atk", s.Stats.SpAttack), ("Sp. Def", s.Stats.SpDefense), ("Speed", s.Stats.Speed) };
        foreach (var (label, value) in stats.Where(x => x.Item2 is < 1 or > BaseStats.Max))
        {
            Add(IssueSeverity.Error, $"{label} is {value}; it must be between 1 and {BaseStats.Max}.");
        }

        if (s.CatchRate is < 0 or > 255)
        {
            Add(IssueSeverity.Error, $"catch rate is {s.CatchRate}; it must be between 0 and 255.");
        }

        if (s.Types.Count == 2 && s.Types[0] == s.Types[1])
        {
            Add(IssueSeverity.Warning, $"has {data.Catalog.NameOf(s.Types[0])} as both types.", "Make it a single-type Pokémon?",
                d => SpeciesChange(d, s.Key, "Types", x => x with { Types = [x.Types[0]] }));
        }

        if (s.Abilities.Count > 0 && s.Abilities[0] == "ABILITY_NONE")
        {
            Add(IssueSeverity.Warning, "has no first ability, so it may end up with none.", "Pick a first ability.");
        }

        var lines = s.DexEntry.Split('\n');
        if (lines.Length > limits.DexEntryMaxLines)
        {
            Add(IssueSeverity.Warning, $"Pokédex entry has {lines.Length} lines; only {limits.DexEntryMaxLines} fit on screen.");
        }

        var longLine = lines.FirstOrDefault(l => l.Length > limits.DexEntryLineLength);
        if (longLine is not null)
        {
            Add(IssueSeverity.Warning, $"a Pokédex line is {longLine.Length} characters and may run off the screen (about {limits.DexEntryLineLength} fit).", "Press Enter to break the line.");
        }
    }

    private static void CheckLearnset(GameData data, int index, GameLimits limits, HashSet<string> moveKeys, List<ValidationIssue> issues)
    {
        var l = data.Learnsets[index];
        var who = l.UsedBy.Count > 0 ? string.Join(", ", l.UsedBy.Take(2).Select(k => data.Species.FirstOrDefault(s => s.Key == k)?.DisplayName ?? k)) : l.Key;
        void Add(IssueSeverity sev, string message, string? suggestion = null, Func<GameData, IChange>? fix = null) =>
            issues.Add(new ValidationIssue(sev, "Learnset", who, message, suggestion) { Fix = fix, TargetKey = l.Key });

        var levelOne = l.LevelUp.Where(m => m.Level == 1).ToList();
        if (levelOne.Count > limits.MovesKnown)
        {
            var extra = levelOne.Count - limits.MovesKnown;
            Add(IssueSeverity.Warning,
                $"learns {levelOne.Count} moves at level 1 but can only hold {limits.MovesKnown}.",
                $"Remove the first {extra} so the last {limits.MovesKnown} stay?",
                d => LearnsetChange(d, l.Key, $"Removed {extra} extra level-1 move(s)", x =>
                {
                    var drop = x.LevelUp.Where(m => m.Level == 1).Take(extra).ToHashSet();
                    return x with { LevelUp = x.LevelUp.Where(m => !drop.Contains(m)).ToList() };
                }));
        }

        foreach (var bad in l.LevelUp.Where(m => m.Level < 0 || m.Level > limits.MaxLevel))
        {
            Add(IssueSeverity.Error, $"learns {data.Catalog.NameOf(bad.Move)} at level {bad.Level}; levels go from 1 to {limits.MaxLevel}.");
        }

        var dupes = l.LevelUp.GroupBy(m => m).Where(g => g.Count() > 1).Select(g => g.Key).ToList();
        foreach (var d in dupes)
        {
            Add(IssueSeverity.Warning, $"learns {data.Catalog.NameOf(d.Move)} twice at level {d.Level}.", "Remove the duplicate?",
                g => LearnsetChange(g, l.Key, $"Removed duplicate {g.Catalog.NameOf(d.Move)}", x =>
                {
                    var seen = new HashSet<LevelUpMove>();
                    return x with { LevelUp = x.LevelUp.Where(seen.Add).ToList() };
                }));
        }

        if (moveKeys.Count > 0)
        {
            foreach (var unknown in l.LevelUp.Select(m => m.Move).Concat(l.EggMoves).Where(m => !moveKeys.Contains(m)).Distinct())
            {
                Add(IssueSeverity.Error, $"uses “{unknown}”, which isn't a move in this game.", "Pick a different move.");
            }
        }
    }

    private static void CheckEvolutions(GameData data, EvolutionSet set, GameLimits limits, HashSet<string> speciesKeys, List<ValidationIssue> issues)
    {
        var who = data.Species.FirstOrDefault(s => s.Key == set.FromSpecies)?.DisplayName ?? set.FromSpecies;
        foreach (var e in set.Evolutions)
        {
            if (!speciesKeys.Contains(e.TargetSpecies))
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, "Evolution", who, $"evolves into “{e.TargetSpecies}”, which doesn't exist in this game.", "Pick another Pokémon.") { TargetKey = set.FromSpecies });
            }

            if (e.Method == "EVO_LEVEL" && int.TryParse(e.Parameter, out var lvl) && (lvl < 0 || lvl > limits.MaxLevel))
            {
                issues.Add(new ValidationIssue(IssueSeverity.Error, "Evolution", who, $"evolves at level {lvl}; levels go from 1 to {limits.MaxLevel}.") { TargetKey = set.FromSpecies });
            }
        }

        if (set.Evolutions.Count > limits.MaxEvolutionsPerSpecies)
        {
            issues.Add(new ValidationIssue(IssueSeverity.Warning, "Evolution", who, $"has {set.Evolutions.Count} evolutions; the game handles up to {limits.MaxEvolutionsPerSpecies}.") { TargetKey = set.FromSpecies });
        }
    }

    private static IChange SpeciesChange(GameData data, string key, string field, Func<Species, Species> update)
    {
        var index = data.Species.FindIndex(s => s.Key == key);
        var before = data.Species[index];
        var after = update(before);
        return new ValueChange<Species>("Pokémon", $"{before.DisplayName} · fixed {field.ToLowerInvariant()}", v => data.Species[index] = v, before, after);
    }

    private static IChange LearnsetChange(GameData data, string key, string description, Func<Learnset, Learnset> update)
    {
        var index = data.Learnsets.FindIndex(l => l.Key == key);
        var before = data.Learnsets[index];
        return new ValueChange<Learnset>("Learnsets", description, v => data.Learnsets[index] = v, before, update(before));
    }
}
