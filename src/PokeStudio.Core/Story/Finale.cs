using PokeStudio.Core.ChampionLab;
using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;

namespace PokeStudio.Core.Story;

/// <summary>
/// The final chapter after the Champion: no credits yet. A guide (Steven) calls the player, waits at a meeting place
/// for a battle above the Champion's level, then takes them to an island where a boss's forms attack one after another
/// (chosen against the player's team) and the boss has to be caught. Then the visiting challengers go home and the
/// credits roll.
/// </summary>
public sealed record FinaleSettings
{
    /// <summary>The guide's story battle (a trainer at <see cref="StoryBattle.Map"/>, fought by talking to them).</summary>
    public string Guide { get; init; } = "TRAINER_PS_STEVEN";

    public string GuideName { get; init; } = "Steven";
    public string GuideLook { get; init; } = "OBJ_EVENT_GFX_STEVEN";

    /// <summary>Where the guide calls the player after the Hall of Fame (the player's room; both rooms in Emerald).</summary>
    public IReadOnlyList<string> HomeMaps { get; init; } = [];

    /// <summary>Where the boss is fought, e.g. BirthIsland_Exterior (the player arrives in front of its first entrance).</summary>
    public string IslandMap { get; init; } = "BirthIsland_Exterior";

    public string IslandName { get; init; } = "Birth Island";
    public string MeetName { get; init; } = "";

    /// <summary>The boss's forms, fought one after another without healing.</summary>
    public IReadOnlyList<string> Forms { get; init; } = [];

    public int FormLevel { get; init; } = 93;

    /// <summary>The boss to catch after its forms are beaten.</summary>
    public string Catch { get; init; } = "";

    public int CatchLevel { get; init; } = 90;
    public string BossName { get; init; } = "Deoxys";
    public string Regions { get; init; } = "Kanto and Hoenn";
}

public static class Finale
{
    public const string HomeHide = "FLAG_PS_FIN_HOME_HIDE";
    public const string CaveHide = "FLAG_PS_FIN_CAVE_HIDE";
    public const string IslandHide = "FLAG_PS_FIN_ISLAND_HIDE";
    public const string Tried = "FLAG_PS_FIN_TRIED";
    public const string FormsBeaten = "FLAG_PS_FIN_FORMS";
    public const string Done = "FLAG_PS_FIN_DONE";
    public const string AfterBattleScript = "PokeStudio_Finale_Guide";
    public const string WonScript = "PokeStudio_Finale_ToIsland";

    public static IReadOnlyList<string> Flags { get; } = [HomeHide, CaveHide, IslandHide, Tried, FormsBeaten, Done];

    private static readonly string[] DeoxysForms = ["SPECIES_DEOXYS_NORMAL", "SPECIES_DEOXYS_SPEED", "SPECIES_DEOXYS_ATTACK", "SPECIES_DEOXYS_DEFENSE"];

    /// <summary>FireRed: Steven calls you home, waits in Cerulean Cave, and takes you to Birth Island.</summary>
    public static (FinaleSettings Settings, string MeetMap) FireRed { get; } = (new FinaleSettings
    {
        HomeMaps = ["PalletTown_PlayersHouse_2F"],
        MeetName = "Cerulean Cave",
        Forms = DeoxysForms,
        Catch = "SPECIES_DEOXYS_NORMAL",
    }, "CeruleanCave_1F");

    /// <summary>Emerald: Steven calls you home, waits in his room in Granite Cave, and takes you to Birth Island.</summary>
    public static (FinaleSettings Settings, string MeetMap) Emerald { get; } = (new FinaleSettings
    {
        HomeMaps = ["LittlerootTown_BrendansHouse_2F", "LittlerootTown_MaysHouse_2F"],
        MeetName = "Granite Cave",
        Forms = DeoxysForms,
        Catch = "SPECIES_DEOXYS_NORMAL",
    }, "GraniteCave_StevensRoom");

    /// <summary>
    /// Adds the final chapter as one undo step: the guide's trainer (a Champion Lab team at <paramref name="guideLevel"/>,
    /// with the level cap after the Champion's) standing at <paramref name="spot"/>, and the chapter's settings.
    /// </summary>
    public static IChange? Add(GameData data, FinaleSettings settings, StorySpot spot, int guideLevel, string trainerClass, string pic, int nameLimit)
    {
        if (data.Trainers.Any(t => t.Key == settings.Guide))
        {
            return null;
        }

        var parts = new List<IChange>();
        void Do(IChange change)
        {
            change.Apply();
            parts.Add(change);
        }

        try
        {
            var capitals = data.Trainers.Count(t => t.Name.Length > 1 && !t.Name.Any(char.IsLower)) > data.Trainers.Count / 2;
            var name = settings.GuideName.Length <= nameLimit ? settings.GuideName : settings.GuideName[..nameLimit];
            var sameClass = data.Trainers.FirstOrDefault(t => t.TrainerClass == trainerClass && t.Party.Count > 0);
            var trainer = new Trainer
            {
                Id = data.Trainers.Select(t => t.Id).DefaultIfEmpty(0).Max() + 1,
                Key = settings.Guide,
                Name = capitals ? name.ToUpperInvariant() : name,
                TrainerClass = trainerClass,
                Pic = pic,
                Gender = "Male",
                Music = sameClass?.Music ?? "",
                AiFlags = sameClass?.AiFlags ?? [],
            };

            // a team above the Champion's: Steel types, with the Champion's legendary-and-mythical rule
            var lab = data.Lab ?? new LabSettings();
            if (!lab.Themes.ContainsKey(trainer.Key))
            {
                var themed = lab with
                {
                    Themes = new Dictionary<string, TypeTheme>(lab.Themes) { [trainer.Key] = new TypeTheme("TYPE_STEEL", 3) },
                    GroupOverrides = new Dictionary<string, TrainerGroup>(lab.GroupOverrides) { [trainer.Key] = TrainerGroup.Champion },
                };
                Do(new ValueChange<LabSettings?>("Champion Lab", $"{settings.GuideName}: Steel theme", v => data.Lab = v, data.Lab, themed));
                lab = themed;
            }

            var team = TeamBuilder.Suggest(data, lab, trainer, new TeamBuilder.Options(Difficulty.Normal, 6, guideLevel));
            trainer = trainer with { Party = team.Members.Select(m => m.Pokemon).ToList() };
            if (trainer.Party.Count == 0)
            {
                return null;
            }

            var battle = new StoryBattle
            {
                TrainerKey = trainer.Key,
                Map = spot.Map,
                Entrance = settings.MeetName,
                X = spot.X,
                Y = spot.Y,
                Elevation = spot.Elevation,
                Facing = Facing.Down,
                Sight = 0,
                Look = settings.GuideLook,
                Intro = $"{settings.GuideName}: You came. {settings.Regions}\\nare still pulling into each other.\\pBefore I tell you why, show me\\nthe strength of a Champion!",
                Defeat = "Remarkable. You really are\\nthe one who can end this.",
                After = "",
                Before = StoryBattles.AfterLast,
                Hidden = CaveHide,
                OnWin = WonScript,
                AfterTalk = AfterBattleScript,
            };
            Do(StoryBattles.Add(data, battle, trainer, guideLevel));
            var story = data.Story ?? new StorySettings();
            Do(new ValueChange<StorySettings?>("Story battles", "Final chapter", v => data.Story = v, data.Story, story with { Finale = settings }));
            return new CompositeChange("Story battles", $"Final chapter: {settings.GuideName}, {settings.IslandName} and {settings.BossName}", parts);
        }
        finally
        {
            for (var i = parts.Count - 1; i >= 0; i--)
            {
                parts[i].Revert();
            }
        }
    }

    /// <summary>Removes the final chapter (its guide battle too) as one undo step.</summary>
    public static IChange? Remove(GameData data)
    {
        if (data.Story is not { Finale: { } finale } story)
        {
            return null;
        }

        // each part is worked out on the data as the one before leaves it, then everything is put back
        var settings = new ValueChange<StorySettings?>("Story battles", "Final chapter removed", v => data.Story = v, story, story with { Finale = null });
        settings.Apply();
        var guide = StoryBattles.Remove(data, finale.Guide);
        settings.Revert();
        return new CompositeChange("Story battles", "Final chapter removed", guide is null ? [settings] : [settings, guide]);
    }
}
