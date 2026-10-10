using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Modernize;

/// <summary>A generation the user can pick, e.g. (9, "Gen 9 — Scarlet & Violet (latest)").</summary>
public sealed record GenerationChoice(int Generation, string Label, bool IsLatest = false);

/// <summary>Base stats, types and abilities as a generation's games have them.</summary>
public sealed record SpeciesUpdate(BaseStats Stats, IReadOnlyList<string> Types, IReadOnlyList<string> Abilities);

/// <summary>What a generation's games use, keyed like the loaded <see cref="GameData"/>.</summary>
/// <param name="LevelUp">Learnset key → level-up moves.</param>
/// <param name="Species">Species key → stats, types and abilities.</param>
/// <param name="Notes">Plain-English notes shown on the page (e.g. which parts are already up to date).</param>
public sealed record ModernizeReference(
    int Generation,
    IReadOnlyDictionary<string, IReadOnlyList<LevelUpMove>> LevelUp,
    IReadOnlyDictionary<string, SpeciesUpdate> Species,
    IReadOnlyList<string> Notes);

/// <summary>What Modernize would change for one Pokémon.</summary>
public sealed record ModernizePlanRow
{
    public required string SpeciesKey { get; init; }
    public required string Title { get; init; }

    /// <summary>Learnset key changed with this row (shared lists are changed once, on their first Pokémon).</summary>
    public string? LearnsetKey { get; init; }

    public IReadOnlyList<LevelUpMove>? NewLevelUp { get; init; }
    public IReadOnlyList<string> MovesAdded { get; init; } = [];
    public IReadOnlyList<string> MovesRemoved { get; init; } = [];
    public SpeciesUpdate? NewSpecies { get; init; }

    /// <summary>"Speed 45 → 60", "type Grass → Grass/Fairy" …</summary>
    public IReadOnlyList<string> SpeciesChanges { get; init; } = [];

    /// <summary>Other Pokémon that share this level-up list.</summary>
    public IReadOnlyList<string> SharedWith { get; init; } = [];

    public bool ChangesMoves => NewLevelUp is not null;
    public bool ChangesSpecies => NewSpecies is not null;
}

/// <summary>Compares a project with a generation's data and turns the result into one undoable change.</summary>
public static class Modernizer
{
    public static IReadOnlyList<ModernizePlanRow> Plan(GameData data, ModernizeReference reference, bool moves, bool species)
    {
        var rows = new List<ModernizePlanRow>();
        var learnsetsSeen = new HashSet<string>(StringComparer.Ordinal);
        var coveredSpecies = new HashSet<string>(StringComparer.Ordinal); // share a level-up list already planned
        var c = data.Catalog;
        foreach (var s in data.Species.Where(s => s.IsEnabled && !s.LockedFields.ContainsKey("*")))
        {
            string? learnsetKey = null;
            IReadOnlyList<LevelUpMove>? newLevelUp = null;
            List<string> added = [], removed = [];
            IReadOnlyList<string> shared = [];
            if (moves && !coveredSpecies.Contains(s.Key) && data.LearnsetFor(s) is { } current && !current.LockedParts.ContainsKey("levelUp")
                && reference.LevelUp.TryGetValue(current.Key, out var target)
                && learnsetsSeen.Add(current.Key)
                && !current.LevelUp.SequenceEqual(target))
            {
                learnsetKey = current.Key;
                newLevelUp = target;
                var before = current.LevelUp.Select(m => m.Move).ToHashSet(StringComparer.Ordinal);
                var after = target.Select(m => m.Move).ToHashSet(StringComparer.Ordinal);
                added = target.Select(m => m.Move).Where(m => !before.Contains(m)).Distinct().ToList();
                removed = current.LevelUp.Select(m => m.Move).Where(m => !after.Contains(m)).Distinct().ToList();
                shared = current.UsedBy.Where(k => k != s.Key).ToList();
                coveredSpecies.UnionWith(shared);
            }

            SpeciesUpdate? update = null;
            var changes = new List<string>();
            if (species && reference.Species.TryGetValue(s.Key, out var u))
            {
                if (!s.LockedFields.ContainsKey("Stats") && u.Stats != s.Stats)
                {
                    changes.AddRange(StatChanges(s.Stats, u.Stats));
                }

                if (!s.LockedFields.ContainsKey("Types") && u.Types.Count > 0 && !u.Types.SequenceEqual(s.Types))
                {
                    changes.Add($"type {string.Join("/", s.Types.Select(c.NameOf))} → {string.Join("/", u.Types.Select(c.NameOf))}");
                }

                if (!s.LockedFields.ContainsKey("Abilities") && u.Abilities.Count > 0 && !u.Abilities.SequenceEqual(s.Abilities))
                {
                    changes.Add($"abilities {Abilities(s.Abilities, c)} → {Abilities(u.Abilities, c)}");
                }

                if (changes.Count > 0)
                {
                    update = u;
                }
            }

            if (newLevelUp is not null || update is not null)
            {
                rows.Add(new ModernizePlanRow
                {
                    SpeciesKey = s.Key,
                    Title = s.DisplayName,
                    LearnsetKey = learnsetKey,
                    NewLevelUp = newLevelUp,
                    MovesAdded = added,
                    MovesRemoved = removed,
                    NewSpecies = update,
                    SpeciesChanges = changes,
                    SharedWith = shared,
                });
            }
        }

        return rows;
    }

    /// <summary>One change for all chosen rows, so a single Undo puts everything back.</summary>
    public static IChange ToChange(GameData data, IEnumerable<ModernizePlanRow> rows, string generationLabel)
    {
        var parts = new List<IChange>();
        var count = 0;
        foreach (var row in rows)
        {
            count++;
            if (row.NewLevelUp is { } levelUp && row.LearnsetKey is { } key)
            {
                // every learnset using this level-up list (forms can share it but differ in TM lists)
                for (var i = 0; i < data.Learnsets.Count; i++)
                {
                    var l = data.Learnsets[i];
                    if (l.Key != key && !(l.UsedBy.Contains(row.SpeciesKey) && !l.LockedParts.ContainsKey("levelUp")))
                    {
                        continue;
                    }

                    var index = i;
                    parts.Add(new ValueChange<Learnset>("Learnsets", $"{row.Title} · level-up moves from {generationLabel}",
                        v => data.Learnsets[index] = v, l, l with { LevelUp = levelUp.ToList() }));
                }
            }

            if (row.NewSpecies is { } u)
            {
                var i = data.Species.FindIndex(s => s.Key == row.SpeciesKey);
                if (i >= 0)
                {
                    var before = data.Species[i];
                    var after = before;
                    if (!before.LockedFields.ContainsKey("Stats"))
                    {
                        after = after with { Stats = u.Stats };
                    }

                    if (!before.LockedFields.ContainsKey("Types") && u.Types.Count > 0)
                    {
                        after = after with { Types = u.Types.ToList() };
                    }

                    if (!before.LockedFields.ContainsKey("Abilities") && u.Abilities.Count > 0)
                    {
                        after = after with { Abilities = u.Abilities.ToList() };
                    }

                    parts.Add(new ValueChange<Species>("Pokémon", $"{row.Title} · stats, types and abilities from {generationLabel}",
                        v => data.Species[i] = v, before, after));
                }
            }
        }

        return new CompositeChange("Modernize", $"Modernized {count} Pokémon to {generationLabel}", parts);
    }

    private static IEnumerable<string> StatChanges(BaseStats a, BaseStats b)
    {
        (string, int, int)[] all =
        [
            ("HP", a.Hp, b.Hp), ("Attack", a.Attack, b.Attack), ("Defense", a.Defense, b.Defense),
            ("Sp. Atk", a.SpAttack, b.SpAttack), ("Sp. Def", a.SpDefense, b.SpDefense), ("Speed", a.Speed, b.Speed),
        ];
        return all.Where(x => x.Item2 != x.Item3).Select(x => $"{x.Item1} {x.Item2} → {x.Item3}");
    }

    private static string Abilities(IReadOnlyList<string> list, GameCatalog c) =>
        string.Join(", ", list.Where(a => a != "ABILITY_NONE").Select(c.NameOf));
}
