using System.Text.RegularExpressions;
using PokeStudio.Core.Changes;
using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Story;

public enum Facing
{
    Down,
    Up,
    Left,
    Right,
}

/// <summary>
/// A battle the player can't skip: a trainer stands next to an entrance (a gym door, a cave mouth…) and looks at the tile
/// in front of it, so everyone who goes in is spotted and challenged. The game's own trainer-sight code starts the
/// battle and its trainer flag remembers the win, so no extra flags or variables are needed.
/// </summary>
public sealed record StoryBattle
{
    /// <summary>The trainer fought, e.g. TRAINER_PS_ROXANNE (an ordinary trainer: edit the team on the Trainers page).</summary>
    public required string TrainerKey { get; init; }

    /// <summary>Map folder, e.g. "PewterCity".</summary>
    public required string Map { get; init; }

    /// <summary>What the entrance leads to, e.g. "Pewter City Gym".</summary>
    public required string Entrance { get; init; }

    /// <summary>Where the trainer stands and which way they look (at the tile in front of the entrance).</summary>
    public int X { get; init; }
    public int Y { get; init; }
    public int Elevation { get; init; }
    public Facing Facing { get; init; }
    public int Sight { get; init; }

    /// <summary>The overworld sprite, e.g. OBJ_EVENT_GFX_COOLTRAINER_F.</summary>
    public string Look { get; init; } = "OBJ_EVENT_GFX_COOLTRAINER_F";

    public string Intro { get; init; } = "";
    public string Defeat { get; init; } = "";
    public string After { get; init; } = "";

    /// <summary>
    /// The level-cap step this battle comes before (e.g. FLAG_BADGE01_GET): the battle becomes a major battle with its
    /// own cap. Null: an ordinary battle.
    /// </summary>
    public string? Before { get; init; }

    /// <summary>An item the player gets after the first win (a TM of the trainer's type), or null.</summary>
    public string? Reward { get; init; }

    /// <summary>A flag that hides the trainer while it's set (the final chapter's guide waits hidden until called).</summary>
    public string? Hidden { get; init; }

    /// <summary>Script run straight after the first win instead of the reward (the final chapter's guide).</summary>
    public string? OnWin { get; init; }

    /// <summary>Script run when the player talks to the trainer after the battle instead of the "after" line.</summary>
    public string? AfterTalk { get; init; }

    /// <summary>The entrance the trainer guards (its door or cave mouth) and the tile in front of it.</summary>
    public int? DoorX { get; init; }
    public int? DoorY { get; init; }
    public int? FrontX { get; init; }
    public int? FrontY { get; init; }

    /// <summary>
    /// The trainer waits inside and comes out to meet the player who steps in front of the entrance, then walks away
    /// after losing (needs <see cref="DoorX"/>…<see cref="FrontY"/>; otherwise they stand outside looking at it).
    /// </summary>
    public bool ComesOut { get; init; } = true;

    /// <summary>Whether the come-out-and-battle scene is used (the final chapter's guide keeps its own ways).</summary>
    public bool UsesScene => ComesOut && DoorX is not null && DoorY is not null && FrontX is not null && FrontY is not null
        && Hidden is null && OnWin is null && AfterTalk is null;

    /// <summary>The name used for the battle's flag and script labels, e.g. "ROXANNE".</summary>
    public string Id => StoryBattles.IdOf(TrainerKey);

    /// <summary>The flag that is set when the battle is won (used by the level caps).</summary>
    public string BeatFlag => "FLAG_PS_BEAT_" + Id;
}

/// <summary>The story battles of a project. Saved with the Poke Studio project; written into the game on save.</summary>
public sealed record StorySettings
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<StoryBattle> Battles { get; init; } = [];

    /// <summary>Sprites to bring from another project on the next save (they are written into the game, then kept here).</summary>
    public IReadOnlyList<StoryImport> Imports { get; init; } = [];

    /// <summary>The final chapter after the Champion, or null.</summary>
    public FinaleSettings? Finale { get; init; }

    /// <summary>Legendary and mythical Pokémon on the maps, revealed by story battles (see <see cref="Story.Legends"/>).</summary>
    public IReadOnlyList<StoryLegend> Legends { get; init; } = [];
}

/// <summary>A place a story battle can go: the tile in front of an entrance and a spot that looks at it.</summary>
public sealed record StorySpot(string Map, string Entrance, int DoorX, int DoorY, int FrontX, int FrontY, int X, int Y, int Elevation, Facing Facing, int Sight)
{
    public string Label => $"{Entrance} (door at {DoorX}, {DoorY})";
}

public static partial class StoryBattles
{
    public const string TrainerPrefix = "TRAINER_PS_";

    /// <summary>A <see cref="StoryBattle.Before"/> value: after every other level-cap step (the final chapter's battle).</summary>
    public const string AfterLast = "@end";

    /// <summary>A new trainer's key from a name: "Tate & Liza" → TRAINER_PS_TATE_AND_LIZA (unique in the project).</summary>
    public static string NewTrainerKey(GameData data, string name)
    {
        var id = NonWord().Replace(name.Replace("&", " and ", StringComparison.Ordinal).ToUpperInvariant(), "_").Trim('_');
        var key = TrainerPrefix + (id.Length == 0 ? "STORY" : id);
        var unique = key;
        for (var i = 2; data.Trainers.Any(t => t.Key == unique); i++)
        {
            unique = $"{key}_{i}";
        }

        return unique;
    }

    public static string IdOf(string trainerKey) =>
        trainerKey.StartsWith(TrainerPrefix, StringComparison.Ordinal) ? trainerKey[TrainerPrefix.Length..] : trainerKey.Replace("TRAINER_", "", StringComparison.Ordinal);

    public static string DefaultIntro(string name, string? title) =>
        $"I'm {name}{(string.IsNullOrEmpty(title) ? "" : ", " + title)}.\\nNobody goes in there without\\lgetting past me first!";

    public static string DefaultDefeat(string name) => "Well fought!\\nYou've earned the right to go on.";

    public static string DefaultAfter(string name) => "Go on. You've proven yourself\\nto me.";

    /// <summary>
    /// Adds a story battle with its new trainer as one undo step. The trainer starts with <paramref name="party"/>; its
    /// team can then be edited or redesigned like any other.
    /// </summary>
    public static IChange Add(GameData data, StoryBattle battle, Trainer trainer, int? capLevel = null)
    {
        var before = data.Story ?? new StorySettings();
        var parts = new List<IChange>
        {
            new ListInsertChange<Trainer>("Trainers", trainer.Key, data.Trainers, trainer, data.Trainers.Count),
            new ValueChange<StorySettings?>("Story battles", battle.Entrance, v => data.Story = v, data.Story, before with { Battles = [.. before.Battles, battle] }),
        };

        // a major battle gets its own level-cap step, just before the step it comes before
        if (battle.Before is { } next && data.Rules is { } rules
            && (next == AfterLast ? rules.LevelCaps.Count : rules.LevelCaps.ToList().FindIndex(c => c.Milestone == next)) is var at and >= 0
            && !rules.LevelCaps.Any(c => c.Milestone == battle.BeatFlag))
        {
            var cap = new LevelCap(battle.BeatFlag, GameCatalog.Prettify(trainer.Name), capLevel ?? trainer.Party.Select(p => p.Level).DefaultIfEmpty(rules.LevelCaps.Count > 0 ? rules.LevelCaps[Math.Min(at, rules.LevelCaps.Count - 1)].Level : 50).Max());
            var caps = rules.LevelCaps.ToList();
            caps.Insert(at, cap);
            parts.Add(new ValueChange<GameRules?>("Level caps", $"Level cap for {trainer.Name}", v => data.Rules = v, rules, rules with { LevelCaps = caps }));
        }

        return new CompositeChange("Story battles", $"Story battle: {GameCatalog.Prettify(trainer.Name)} at {battle.Entrance}", parts);
    }

    /// <summary>Removes a story battle, its trainer and its level-cap step, as one undo step.</summary>
    public static IChange? Remove(GameData data, string trainerKey)
    {
        if (data.Story is not { } story || story.Battles.FirstOrDefault(b => b.TrainerKey == trainerKey) is not { } battle)
        {
            return null;
        }

        var parts = new List<IChange>
        {
            new ValueChange<StorySettings?>("Story battles", battle.Entrance, v => data.Story = v, story, story with { Battles = story.Battles.Where(b => b != battle).ToList() }),
        };
        if (data.Trainers.FirstOrDefault(t => t.Key == trainerKey) is { } trainer)
        {
            parts.Add(new ListRemoveChange<Trainer>("Trainers", trainerKey, data.Trainers, trainer));
        }

        // its difficulty versions go too
        foreach (var version in data.Trainers.Where(t => t.Key.StartsWith(trainerKey + "@", StringComparison.Ordinal)).ToList())
        {
            parts.Add(new ListRemoveChange<Trainer>("Trainers", version.Key, data.Trainers, version));
        }

        if (data.Rules is { } rules && rules.LevelCaps.Any(c => c.Milestone == battle.BeatFlag))
        {
            parts.Add(new ValueChange<GameRules?>("Level caps", "Level cap removed", v => data.Rules = v, rules,
                rules with { LevelCaps = rules.LevelCaps.Where(c => c.Milestone != battle.BeatFlag).ToList() }));
        }

        var name = GameCatalog.Prettify(data.Trainers.FirstOrDefault(t => t.Key == trainerKey)?.Name ?? battle.Id);
        return new CompositeChange("Story battles", $"Story battle removed: {name}", parts);
    }

    /// <summary>"MAP_PEWTER_CITY_GYM" or the map folder "PewterCity_Gym" → "Pewter City Gym".</summary>
    public static string PlaceName(string map) => map.StartsWith("MAP_", StringComparison.Ordinal)
        ? GameCatalog.Prettify(map[4..])
        : Spaces().Replace(Words().Replace(map, " ").Replace('_', ' '), " ").Trim();

    /// <summary>"OBJ_EVENT_GFX_COOLTRAINER_F" → "Cooltrainer F".</summary>
    public static string LookName(string look) => GameCatalog.Prettify(look.Replace("OBJ_EVENT_GFX_", "", StringComparison.Ordinal));

    [GeneratedRegex(@"(?<=[a-z])(?=[A-Z0-9])|(?<=[0-9])(?=[A-Z][a-z])")]
    private static partial Regex Words();

    [GeneratedRegex(@"\s+")]
    private static partial Regex Spaces();

    [GeneratedRegex(@"[^A-Z0-9]+")]
    private static partial Regex NonWord();
}

/// <summary>Removes an item from a list; undo puts it back where it was.</summary>
public sealed class ListRemoveChange<T>(string area, string description, List<T> list, T item) : IChange
    where T : class
{
    private int _index = -1;

    public string Area { get; } = area;
    public string Description { get; } = description;

    public void Apply()
    {
        _index = list.IndexOf(item);
        list.Remove(item);
    }

    public void Revert() => list.Insert(Math.Clamp(_index < 0 ? list.Count : _index, 0, list.Count), item);
}
