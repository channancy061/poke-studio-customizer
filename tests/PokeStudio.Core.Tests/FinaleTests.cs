using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Story;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

/// <summary>The final chapter: its step after the Champion's cap, the people on its maps, and whole chapters on the user's real projects.</summary>
public class FinaleTests
{
    private const string CapsText = "#include \"global.h\"\n#include \"caps.h\"\n\nu32 GetCurrentLevelCap(void)\n{\n    static const u32 sLevelCapFlagMap[][2] =\n    {\n        {FLAG_BADGE01_GET,    14},\n        {FLAG_IS_CHAMPION,    85},\n    };\n}\n";

    private static GameData Data()
    {
        var data = new GameData();
        data.Trainers.Add(new Trainer { Id = 1, Key = "TRAINER_LEADER_BROCK", Name = "BROCK", Party = [new TrainerPokemon { Species = "SPECIES_ONIX", Level = 14 }] });
        data.Rules = new GameRules { LevelCaps = [new LevelCap("FLAG_BADGE01_GET", "Gym 1", 14), new LevelCap("FLAG_IS_CHAMPION", "Champion", 85)] };
        return data;
    }

    [Fact]
    public void A_battle_after_the_last_gets_a_cap_step_after_the_champions_and_saving_again_changes_nothing()
    {
        var data = Data();
        var steven = new Trainer { Id = 2, Key = "TRAINER_PS_STEVEN", Name = "STEVEN", Party = [new TrainerPokemon { Species = "SPECIES_METAGROSS", Level = 90 }] };
        var battle = new StoryBattle
        {
            TrainerKey = steven.Key, Map = "CeruleanCave_1F", Entrance = "Cerulean Cave", X = 5, Y = 5, Sight = 0,
            Intro = "Hi", Defeat = "Bye", Before = StoryBattles.AfterLast, Hidden = Finale.CaveHide,
        };
        StoryBattles.Add(data, battle, steven, 90).Apply();
        Assert.Equal(["FLAG_BADGE01_GET", "FLAG_IS_CHAMPION", "FLAG_PS_BEAT_STEVEN"], data.Rules!.LevelCaps.Select(c => c.Milestone));

        var once = StoryWriter.Caps(CapsText, data.Story!.Battles, data);
        Assert.Contains("        {FLAG_IS_CHAMPION,    85},\n        {FLAG_PS_BEAT_STEVEN, 90}, // Poke Studio story battle\n    };", once, StringComparison.Ordinal);
        Assert.Equal(once, StoryWriter.Caps(once, data.Story.Battles, data));
        Assert.Equal(CapsText, StoryWriter.Caps(once, [], data));
    }

    [Fact]
    public void People_without_sight_just_stand_there_and_hide_behind_their_flag()
    {
        const string json = "{\n  \"object_events\": [],\n  \"warp_events\": []\n}\n";
        var person = new StoryWriter.MapPerson("PokeStudio_Story_FinaleHome", "OBJ_EVENT_GFX_STEVEN", 3, 4, 3, Facing.Down, 0, Finale.HomeHide);
        var written = StoryWriter.MapObjects(json, [person]);
        var ours = System.Text.Json.JsonDocument.Parse(written).RootElement.GetProperty("object_events")[0];
        Assert.Equal("TRAINER_TYPE_NONE", ours.GetProperty("trainer_type").GetString());
        Assert.Equal(Finale.HomeHide, ours.GetProperty("flag").GetString());
        Assert.Equal("PokeStudio_Story_FinaleHome", ours.GetProperty("script").GetString());
        Assert.Equal(json, StoryWriter.MapObjects(written, Array.Empty<StoryWriter.MapPerson>()));

        // a challenger leaves once the chapter is over; its own hiding flag wins
        var challenger = new StoryBattle { TrainerKey = "TRAINER_PS_BROCK", Map = "M", Entrance = "E", Sight = 3 };
        Assert.Equal(Finale.Done, StoryWriter.PersonOf(challenger, Finale.Done).Flag);
        Assert.Equal(Finale.CaveHide, StoryWriter.PersonOf(challenger with { Hidden = Finale.CaveHide }, Finale.Done).Flag);
        Assert.Equal("0", StoryWriter.PersonOf(challenger, null).Flag);
    }

    private static string? Dir(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } d && Directory.Exists(d) ? d : null;

    private static async Task WholeChapter(string? root, (FinaleSettings Settings, string MeetMap) preset, string symbolsFile)
    {
        if (root is null)
        {
            return;
        }

        using var tmp = new TempDir();
        var module = new ExpansionModule();
        var data = await module.LoadDataAsync(root, tmp.Combine("data"));
        var spot = module.FreeSpot(data, preset.MeetMap);
        Assert.NotNull(spot);
        Assert.NotNull(StoryWriter.Arrival(root, preset.Settings.IslandMap));

        var change = Finale.Add(data, preset.Settings, spot!, 90, "TRAINER_CLASS_CHAMPION", "TRAINER_PIC_STEVEN", module.Limits.TrainerNameLength);
        Assert.NotNull(change);
        Assert.Null(data.Story?.Finale); // Add leaves the data as it was; the change does the rest
        change!.Apply();
        Assert.NotNull(data.Story!.Finale);
        Assert.Equal("FLAG_PS_BEAT_STEVEN", data.Rules!.LevelCaps[^1].Milestone);
        var steven = data.Trainers.Single(t => t.Key == "TRAINER_PS_STEVEN");
        Assert.Equal(6, steven.Party.Count);
        Assert.All(steven.Party, p => Assert.Equal(90, p.Level));
        Assert.Null(Finale.Add(data, preset.Settings, spot!, 90, "TRAINER_CLASS_CHAMPION", "TRAINER_PIC_STEVEN", module.Limits.TrainerNameLength));

        // the files: game code, its own scripts, the Hall of Fame hold and Steven on every map he visits
        string? Read(string rel) => File.Exists(Path.Combine(root, rel)) ? File.ReadAllText(Path.Combine(root, rel)) : null;
        var files = StoryWriter.Plan(root, data, Read);
        Assert.Contains("PokeStudio_PrepareFinalBoss", files[FinaleWriter.Code], StringComparison.Ordinal);
        Assert.Contains("gPokeStudioFinaleNoCatch", files[FinaleWriter.ItemUse], StringComparison.Ordinal);
        Assert.Contains(Finale.CaveHide, files[FinaleWriter.NewGame], StringComparison.Ordinal);
        Assert.Contains(Finale.Done, files[GameOptionsWriter.Header], StringComparison.Ordinal);
        Assert.Contains(files.Keys, f => f.EndsWith($"/{preset.MeetMap}/map.json", StringComparison.Ordinal));
        Assert.Contains(files.Keys, f => f.EndsWith($"/{preset.Settings.IslandMap}/map.json", StringComparison.Ordinal));
        Assert.All(preset.Settings.HomeMaps, m => Assert.Contains(files.Keys, f => f.EndsWith($"/{m}/map.json", StringComparison.Ordinal)));
        Assert.Contains("FLAG_PS_BEAT_STEVEN", files.Single(f => f.Key.EndsWith("caps.c", StringComparison.Ordinal)).Value, StringComparison.Ordinal);
        Assert.Contains(symbolsFile, string.Join("\n", files.Keys), StringComparison.Ordinal);

        // removing it is one step, and undoing puts it back
        var remove = Finale.Remove(data)!;
        remove.Apply();
        Assert.Null(data.Story.Finale);
        Assert.DoesNotContain(data.Trainers, t => t.Key == "TRAINER_PS_STEVEN");
        remove.Revert();
        Assert.NotNull(data.Story.Finale);
        change.Revert();
        Assert.True(data.Story is null || data.Story.Finale is null);
    }

    [Fact]
    public Task Real_firered_takes_the_final_chapter_when_available() =>
        WholeChapter(Dir("POKESTUDIO_FIRERED_DIR"), Finale.FireRed, "IndigoPlateau_Exterior");

    [Fact]
    public Task Real_emerald_takes_the_final_chapter_when_available() =>
        WholeChapter(Dir("POKESTUDIO_EXPANSION_DIR"), Finale.Emerald, FinaleWriter.HallOfFameC);
}
