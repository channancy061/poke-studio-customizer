using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Story;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

/// <summary>Story battles: the files they write, adding and removing them, and spots on a real map.</summary>
public class StoryBattleTests
{
    private static StoryBattle Battle(string key, string? before = "FLAG_BADGE01_GET") => new()
    {
        TrainerKey = key,
        Map = "PewterCity",
        Entrance = "Pewter City Gym",
        X = 13, Y = 17, Elevation = 3, Facing = Facing.Right, Sight = 2,
        Intro = "Hi!", Defeat = "Bye!", After = "Go on.",
        Before = before,
    };

    private static GameData Data()
    {
        var data = new GameData();
        data.Trainers.Add(new Trainer { Id = 1, Key = "TRAINER_LEADER_BROCK", Name = "BROCK", Party = [new TrainerPokemon { Species = "SPECIES_ONIX", Level = 14 }] });
        data.Rules = new GameRules { LevelCaps = [new LevelCap("FLAG_BADGE01_GET", "Gym 1", 14), new LevelCap("FLAG_BADGE02_GET", "Gym 2", 21)] };
        return data;
    }

    private static Trainer Roxanne(GameData data) => new()
    {
        Id = 2,
        Key = StoryBattles.NewTrainerKey(data, "Roxanne"),
        Name = "ROXANNE",
        Party = [new TrainerPokemon { Species = "SPECIES_NOSEPASS", Level = 13 }],
    };

    [Fact]
    public void Adding_a_major_battle_adds_its_trainer_and_cap_step_as_one_undo()
    {
        var data = Data();
        var trainer = Roxanne(data);
        Assert.Equal("TRAINER_PS_ROXANNE", trainer.Key);
        var change = StoryBattles.Add(data, Battle(trainer.Key), trainer);
        change.Apply();

        Assert.Contains(data.Trainers, t => t.Key == "TRAINER_PS_ROXANNE");
        Assert.Equal(["FLAG_PS_BEAT_ROXANNE", "FLAG_BADGE01_GET", "FLAG_BADGE02_GET"], data.Rules!.LevelCaps.Select(c => c.Milestone));
        Assert.Equal(13, data.Rules.LevelCaps[0].Level);
        Assert.Equal("Roxanne", data.Rules.LevelCaps[0].Label);

        change.Revert();
        Assert.DoesNotContain(data.Trainers, t => t.Key == "TRAINER_PS_ROXANNE");
        Assert.True(data.Story is null || data.Story.Battles.Count == 0);
        Assert.Equal(2, data.Rules!.LevelCaps.Count);

        change.Apply();
        var remove = StoryBattles.Remove(data, "TRAINER_PS_ROXANNE")!;
        remove.Apply();
        Assert.DoesNotContain(data.Trainers, t => t.Key == "TRAINER_PS_ROXANNE");
        Assert.Equal(2, data.Rules!.LevelCaps.Count);
        remove.Revert();
        Assert.Contains(data.Trainers, t => t.Key == "TRAINER_PS_ROXANNE");
        Assert.Equal(3, data.Rules!.LevelCaps.Count);

        Assert.Equal("TRAINER_PS_ROXANNE_2", StoryBattles.NewTrainerKey(data, "Roxanne"));
        Assert.Equal("TRAINER_PS_TATE_AND_LIZA", StoryBattles.NewTrainerKey(data, "Tate & Liza"));
    }

    [Fact]
    public void Trainer_list_gets_our_names_before_the_count_and_loses_old_ones()
    {
        const string text = "enum TrainerID\n{\n    TRAINER_NONE,\n    TRAINER_BRANDON,\n    TRAINERS_COUNT,\n};\n";
        var added = StoryWriter.Opponent(text, ["TRAINER_PS_ROXANNE", "TRAINER_PS_BRAWLY"]);
        Assert.Contains("    TRAINER_BRANDON,\n    TRAINER_PS_ROXANNE, // Poke Studio story battle\n    TRAINER_PS_BRAWLY, // Poke Studio story battle\n    TRAINERS_COUNT,", added, StringComparison.Ordinal);
        Assert.Equal(text, StoryWriter.Opponent(added, []));
        Assert.Equal(added, StoryWriter.Opponent(added, ["TRAINER_PS_ROXANNE", "TRAINER_PS_BRAWLY"]));
    }

    [Fact]
    public void Caps_get_a_beaten_flag_and_a_step_before_the_badge_and_saving_again_changes_nothing()
    {
        const string text = "#include \"global.h\"\n#include \"caps.h\"\n\nu32 GetCurrentLevelCap(void)\n{\n    static const u32 sLevelCapFlagMap[][2] =\n    {\n        {FLAG_BADGE01_GET,    14},\n        {FLAG_BADGE02_GET,    21},\n    };\n}\n";
        var data = Data();
        var trainer = Roxanne(data);
        StoryBattles.Add(data, Battle(trainer.Key), trainer).Apply();
        var once = StoryWriter.Caps(text, data.Story!.Battles, data);
        Assert.Contains("#include \"caps.h\"\n#include \"constants/opponents.h\" // Poke Studio story battle\n#define FLAG_PS_BEAT_ROXANNE (TRAINER_FLAGS_START + TRAINER_PS_ROXANNE) // Poke Studio story battle\n", once, StringComparison.Ordinal);
        Assert.Contains("        {FLAG_PS_BEAT_ROXANNE, 13}, // Poke Studio story battle\n        {FLAG_BADGE01_GET,    14},", once, StringComparison.Ordinal);
        Assert.Equal(once, StoryWriter.Caps(once, data.Story.Battles, data));
        Assert.Equal(text, StoryWriter.Caps(once, [], data));
    }

    [Fact]
    public void Map_gets_one_trainer_object_per_battle_and_old_ones_are_removed()
    {
        const string json = "{\n  \"object_events\": [\n    {\n      \"type\": \"object\",\n      \"graphics_id\": \"OBJ_EVENT_GFX_LASS\",\n      \"script\": \"PewterCity_EventScript_Lass\",\n      \"flag\": \"0\"\n    }\n  ],\n  \"warp_events\": []\n}\n";
        var one = StoryWriter.MapObjects(json, [Battle("TRAINER_PS_ROXANNE")]);
        var parsed = System.Text.Json.JsonDocument.Parse(one).RootElement.GetProperty("object_events");
        Assert.Equal(2, parsed.GetArrayLength());
        var ours = parsed[1];
        Assert.Equal("object", ours.GetProperty("type").GetString());
        Assert.Equal("TRAINER_TYPE_NORMAL", ours.GetProperty("trainer_type").GetString());
        Assert.Equal("2", ours.GetProperty("trainer_sight_or_berry_tree_id").GetString());
        Assert.Equal("MOVEMENT_TYPE_FACE_RIGHT", ours.GetProperty("movement_type").GetString());
        Assert.Equal("PokeStudio_Story_ROXANNE", ours.GetProperty("script").GetString());

        // saving again replaces ours; with none left, the map is as it was
        Assert.Equal(one, StoryWriter.MapObjects(one, [Battle("TRAINER_PS_ROXANNE")]));
        Assert.Equal(json, StoryWriter.MapObjects(one, Array.Empty<StoryBattle>()));

        // ours first in the list, or alone
        const string empty = "{\n  \"object_events\": [],\n  \"warp_events\": []\n}\n";
        var alone = StoryWriter.MapObjects(empty, [Battle("TRAINER_PS_ROXANNE"), Battle("TRAINER_PS_BRAWLY")]);
        Assert.Equal(2, System.Text.Json.JsonDocument.Parse(alone).RootElement.GetProperty("object_events").GetArrayLength());
        var cleared = StoryWriter.MapObjects(alone, Array.Empty<StoryBattle>());
        Assert.Equal(0, System.Text.Json.JsonDocument.Parse(cleared).RootElement.GetProperty("object_events").GetArrayLength());
    }

    [Fact]
    public void Real_firered_gym_door_has_a_spot_that_sees_it_when_available()
    {
        var root = Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_DIR");
        if (string.IsNullOrEmpty(root) || !Directory.Exists(root))
        {
            return;
        }

        var gym = Assert.Single(StoryWriter.Spots(root, "PewterCity"), s => s.Entrance == "Pewter City Gym");
        Assert.Equal((15, 16), (gym.DoorX, gym.DoorY));
        Assert.Equal((15, 17), (gym.FrontX, gym.FrontY));
        Assert.InRange(gym.Sight, 2, 4);
        var looked = gym.Facing switch { Facing.Right => (gym.X + gym.Sight, gym.Y), Facing.Left => (gym.X - gym.Sight, gym.Y), _ => (gym.X, gym.Y - gym.Sight) };
        Assert.Equal((gym.FrontX, gym.FrontY), looked);
        Assert.Contains(StoryWriter.Maps(root), m => m.Map == "PewterCity");

        // Wallace's spot: just outside Victory Road's exit, in the pocket that leads on to Indigo Plateau
        var exit = Assert.Single(StoryWriter.Spots(root, "Route23"), s => s.Entrance == "Victory Road 2F");
        Assert.Equal((18, 29), (exit.FrontX, exit.FrontY));
        Assert.Equal(29, exit.Y);
        Assert.Contains("OBJ_EVENT_GFX_COOLTRAINER_F", StoryWriter.Looks(root));
    }
}
