using PokeStudio.Core.GameOptions;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Safety;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

public class GameOptionsTests
{
    private static readonly GameOptionsSettings Everything = new()
    {
        Difficulty = OptionMode.PlayerChooses,
        TrainingOff = OptionMode.PlayerChooses,
        LevelCap = OptionMode.PlayerChooses,
        NoItemsInBattle = OptionMode.PlayerChooses,
        InfiniteCandy = OptionMode.PlayerChooses,
    };

    private const string Flags = """
        #ifndef GUARD_CONSTANTS_FLAGS_H
        #define GUARD_CONSTANTS_FLAGS_H
        #define FLAG_TEMP_1      0x1
        #define FLAG_UNUSED_0x01F    0x1F // temporary: never picked
        #define FLAG_UNUSED_0x020    0x20 // Unused Flag
        #define FLAG_UNUSED_0x021    0x21 // Unused Flag
        #define FLAG_UNUSED_0x022    0x22 // Unused Flag
        #endif // GUARD_CONSTANTS_FLAGS_H

        """;

    private const string Vars = """
        #define VAR_0x4029     0x4029
        #define VAR_0x402A     0x402A
        #define VAR_0x402B     0x402B
        #define VAR_0x402C     0x402C

        """;

    private static readonly Dictionary<string, string> Project = new(StringComparer.Ordinal)
    {
        [GameOptionsWriter.FlagsFile] = Flags,
        [GameOptionsWriter.VarsFile] = Vars,
        [GameOptionsWriter.EventScripts] = "\t.include \"data/scripts/wild_encounter.inc\"\n",
        ["data/maps/InsideOfTruck/scripts.inc"] = "InsideOfTruck_MapScripts::\n\tmap_script MAP_SCRIPT_ON_LOAD, InsideOfTruck_OnLoad\n\t.byte 0\n\nInsideOfTruck_OnLoad:\n\tend\n",
        ["include/config/battle.h"] = "#define B_VAR_DIFFICULTY            0     // difficulty var\n#define B_VAR_NO_BAG_USE         0     // bag\n",
        ["include/config/caps.h"] = "#define B_EXP_CAP_TYPE                  EXP_CAP_NONE   // x\n#define B_LEVEL_CAP_TYPE                LEVEL_CAP_NONE // y\n#define B_RARE_CANDY_CAP                FALSE // z\n",
        ["src/caps.c"] = "#include \"event_data.h\"\n\nu32 GetCurrentLevelCap(void)\n{\n    return 100;\n}\n",
        [GameOptionsWriter.Pokemon] = """
            void CalculateMonStats(struct Pokemon *mon)
            {
                for (u32 i = 0; i < NUM_STATS; i++)
                {
                    iv[i] = GetMonData(mon, MON_DATA_HP_IV + i);
                    ev[i] = GetMonData(mon, MON_DATA_HP_EV + i);
                }

                SetMonData(mon, MON_DATA_MAX_HP, &newMaxHP);
            }

            """,
        [GameOptionsWriter.PartyMenu] = """
            void ItemUseCB_RareCandy(u8 taskId, TaskFunc task)
            {
                if (cannotUse)
                {
                }
                else
                {
                    RemoveBagItem(gSpecialVar_ItemId, 1);
                }
            }

            static void Other(void)
            {
                RemoveBagItem(gSpecialVar_ItemId, 1);
            }

            """,
        [GameOptionsWriter.ItemsEnum] = "    ITEM_GLIMMORANITE = 873,\n\n    ITEMS_COUNT,\n",
        ["src/data/items.h"] = "    [ITEM_RARE_CANDY] =\n    {\n        .name = ITEM_NAME(\"Rare Candy\"),\n    },\n\n    [ITEM_PP_UP] =\n    {\n    },\n",
    };

    private static string? Read(Dictionary<string, string> files, string rel) => files.GetValueOrDefault(rel);

    private static Dictionary<string, string> Apply(Dictionary<string, string> files, Dictionary<string, string> plan)
    {
        var copy = new Dictionary<string, string>(files, StringComparer.Ordinal);
        foreach (var (k, v) in plan)
        {
            copy[k] = v;
        }

        return copy;
    }

    private static readonly ExpansionLayout Emerald = new(Path.GetTempPath());

    private static Dictionary<string, string> Plan(Dictionary<string, string> files, GameOptionsSettings o)
    {
        var ids = GameOptionsWriter.AllocateIds(f => Read(files, f), _ => false);
        return GameOptionsWriter.Plan(Emerald, o, ids, f => Read(files, f));
    }

    [Fact]
    public void Free_flags_and_variables_are_picked_outside_the_temporary_ones_and_kept_stable()
    {
        var ids = GameOptionsWriter.AllocateIds(f => Read(Project, f), name => name == "FLAG_UNUSED_0x020");
        Assert.Equal("0x21", ids.Flags["FLAG_PS_TRAINING_OFF"]);
        Assert.Equal("0x22", ids.Flags["FLAG_PS_LEVEL_CAP_OFF"]);
        Assert.Equal(["0x4029", "0x402A", "0x402B"], ids.Vars.Values);

        // once written, the same numbers come back even if the free list changes
        var after = Apply(Project, GameOptionsWriter.Plan(Emerald, Everything, ids, f => Read(Project, f)));
        after[GameOptionsWriter.FlagsFile] = after[GameOptionsWriter.FlagsFile].Replace("FLAG_UNUSED_0x021", "FLAG_STORY_THING", StringComparison.Ordinal);
        var again = GameOptionsWriter.AllocateIds(f => Read(after, f), _ => false);
        Assert.Equal(ids.Flags, again.Flags);
        Assert.Equal(ids.Vars, again.Vars);
    }

    [Fact]
    public void Nothing_is_written_when_no_option_is_used_beyond_the_header_and_script()
    {
        Assert.False(new GameOptionsSettings().AnyUsed);
        var plan = Plan(Project, new GameOptionsSettings { Difficulty = OptionMode.AlwaysOn, FixedDifficulty = DifficultyChoice.Hard });
        Assert.DoesNotContain(GameOptionsWriter.Pokemon, plan.Keys);
        Assert.DoesNotContain(GameOptionsWriter.PartyMenu, plan.Keys);
        Assert.DoesNotContain("src/caps.c", plan.Keys);
        Assert.Contains("setvar VAR_PS_DIFFICULTY, 2", plan[GameOptionsWriter.Script], StringComparison.Ordinal);
        Assert.DoesNotContain("msgbox", plan[GameOptionsWriter.Script], StringComparison.Ordinal); // nothing to ask
    }

    [Fact]
    public void Every_option_writes_its_hooks_once()
    {
        var plan = Plan(Project, Everything);
        var after = Apply(Project, plan);

        Assert.Contains("#define FLAG_PS_TRAINING_OFF 0x20", after[GameOptionsWriter.Header], StringComparison.Ordinal);
        Assert.Contains("#include \"constants/poke_studio.h\"", after[GameOptionsWriter.FlagsFile], StringComparison.Ordinal);
        Assert.True(after[GameOptionsWriter.FlagsFile].IndexOf("poke_studio.h", StringComparison.Ordinal) < after[GameOptionsWriter.FlagsFile].LastIndexOf("#endif", StringComparison.Ordinal));
        Assert.EndsWith("\t.include \"data/scripts/poke_studio_options.inc\" @ Poke Studio\n", after[GameOptionsWriter.EventScripts], StringComparison.Ordinal);
        Assert.Contains("\tmap_script MAP_SCRIPT_ON_FRAME_TABLE, PokeStudio_OnFrameTable @ Poke Studio\n\t.byte 0", after["data/maps/InsideOfTruck/scripts.inc"], StringComparison.Ordinal);
        Assert.Contains("#define B_VAR_DIFFICULTY            0x402A     // difficulty var", after["include/config/battle.h"], StringComparison.Ordinal);
        Assert.Contains("#define B_VAR_NO_BAG_USE         0x402B", after["include/config/battle.h"], StringComparison.Ordinal);
        Assert.Contains("EXP_CAP_HARD", after["include/config/caps.h"], StringComparison.Ordinal);
        Assert.Contains("LEVEL_CAP_FLAG_LIST", after["include/config/caps.h"], StringComparison.Ordinal);
        Assert.Contains("#define B_RARE_CANDY_CAP                TRUE", after["include/config/caps.h"], StringComparison.Ordinal);
        Assert.Contains("{\n    if (FlagGet(FLAG_PS_LEVEL_CAP_OFF))", after["src/caps.c"], StringComparison.Ordinal);

        // the IV/EV override comes right after the loop that reads them
        var pokemon = after[GameOptionsWriter.Pokemon];
        Assert.True(pokemon.IndexOf("MON_DATA_HP_EV", StringComparison.Ordinal) < pokemon.IndexOf("FLAG_PS_TRAINING_OFF", StringComparison.Ordinal));
        Assert.True(pokemon.IndexOf("FLAG_PS_TRAINING_OFF", StringComparison.Ordinal) < pokemon.IndexOf("MON_DATA_MAX_HP", StringComparison.Ordinal));

        // only the Rare Candy function keeps the Infinite Candy
        var party = after[GameOptionsWriter.PartyMenu];
        Assert.Contains("if (gSpecialVar_ItemId != ITEM_INFINITE_CANDY) RemoveBagItem(gSpecialVar_ItemId, 1);", party, StringComparison.Ordinal);
        Assert.Contains("static void Other(void)\n{\n    RemoveBagItem(gSpecialVar_ItemId, 1);", party, StringComparison.Ordinal);
        Assert.Contains("    ITEM_INFINITE_CANDY, // Poke Studio\n    ITEMS_COUNT,", after[GameOptionsWriter.ItemsEnum], StringComparison.Ordinal);
        var items = after["src/data/items.h"];
        Assert.True(items.IndexOf("[ITEM_INFINITE_CANDY]", StringComparison.Ordinal) is var at && at > items.IndexOf("Rare Candy", StringComparison.Ordinal) && at < items.IndexOf("[ITEM_PP_UP]", StringComparison.Ordinal));

        // saving again changes nothing
        Assert.Empty(Plan(after, Everything));
    }

    [Fact]
    public void The_script_asks_what_the_player_chooses_and_applies_the_rest()
    {
        var ids = GameOptionsWriter.AllocateIds(f => Read(Project, f), _ => false);
        var script = GameOptionsWriter.BuildScript(Everything with { DefaultDifficulty = DifficultyChoice.Hard, LevelCap = OptionMode.AlwaysOff, NoItemsAlsoWild = true }, ids);
        Assert.Contains("PokeStudio_OnFrameTable::\n\tmap_script_2 VAR_PS_OPTIONS_STATE, 0, PokeStudio_EventScript_NewGameOptions\n\t.2byte 0", script, StringComparison.Ordinal);
        Assert.Contains("\tsetvar VAR_PS_OPTIONS_STATE, 1", script, StringComparison.Ordinal); // runs once
        Assert.Contains("dynmultichoice 0, 0, TRUE, 4, 2, DYN_MULTICHOICE_CB_NONE, PokeStudio_Text_Easy, PokeStudio_Text_Normal, PokeStudio_Text_Hard, PokeStudio_Text_ExtraHard", script, StringComparison.Ordinal);
        Assert.Contains("\tsetflag FLAG_PS_LEVEL_CAP_OFF\n", script, StringComparison.Ordinal);
        Assert.DoesNotContain("PokeStudio_Text_AskLevelCap", script, StringComparison.Ordinal);
        Assert.Contains("PokeStudio_EventScript_NoItemsYes::\n\tsetvar VAR_PS_NO_BAG, 2\n\treturn", script, StringComparison.Ordinal);
        Assert.Contains("PokeStudio_EventScript_CandyYes::\n\tgiveitem ITEM_INFINITE_CANDY\n\treturn", script, StringComparison.Ordinal);
        Assert.True(script.IndexOf("\tend\n", StringComparison.Ordinal) < script.IndexOf("PokeStudio_EventScript_TrainingOffYes::", StringComparison.Ordinal));

        // every line of text fits FireRed's narrower box (208 px at about 6 px a letter → keep lines under 34 letters)
        foreach (var line in script.Split('\n').Where(l => l.StartsWith("\t.string", StringComparison.Ordinal)))
        {
            foreach (var part in line[10..^2].Replace("$", "", StringComparison.Ordinal).Replace("\\p", "\\n", StringComparison.Ordinal).Split("\\n"))
            {
                Assert.True(part.Length <= 32, $"too long: {part}");
            }
        }
    }

    [Fact]
    public void A_start_map_that_already_has_a_frame_table_gets_one_more_entry()
    {
        const string map = "InsideOfTruck_MapScripts::\n\tmap_script MAP_SCRIPT_ON_FRAME_TABLE, InsideOfTruck_OnFrame\n\t.byte 0\n\nInsideOfTruck_OnFrame:\n\tmap_script_2 VAR_X, 0, Something\n\t.2byte 0\n";
        var after = GameOptionsWriter.HookStartMap(map, "InsideOfTruck_MapScripts");
        Assert.Contains("InsideOfTruck_OnFrame:\n\tmap_script_2 VAR_PS_OPTIONS_STATE, 0, PokeStudio_EventScript_NewGameOptions @ Poke Studio\n\tmap_script_2 VAR_X", after, StringComparison.Ordinal);
    }

    private static async Task RealProject(string? real, string startMap)
    {
        if (string.IsNullOrEmpty(real) || !Directory.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var root = tmp.Combine("game");
        foreach (var dir in new[] { "include", "src", "data/maps", "data/text", "data/scripts" })
        {
            ProjectService.CopyDirectory(Path.Combine([real, .. dir.Split('/')]), Path.Combine([root, .. dir.Split('/')]));
        }

        foreach (var f in new[] { "Makefile", "charmap.txt", "data/event_scripts.s" })
        {
            File.Copy(Path.Combine([real, .. f.Split('/')]), Path.Combine([root, .. f.Split('/')]));
        }

        var module = new ExpansionModule();
        var data = await module.LoadDataAsync(root, tmp.Combine("data"));
        Assert.DoesNotContain(data.Items, i => i.Key == "ITEM_INFINITE_CANDY");
        data.GameOptions = Everything;
        var ctx = new ModuleSaveContext(new SafeFileWriter(root, allowedFiles: module.ManagedFiles(root)), root, root);
        await module.SaveDataAsync(data, ctx);

        string ReadRel(string rel) => File.ReadAllText(Path.Combine([root, .. rel.Split('/')]));
        Assert.Contains("FLAG_PS_TRAINING_OFF", ReadRel(GameOptionsWriter.Header), StringComparison.Ordinal);
        Assert.Contains("PokeStudio_OnFrameTable", ReadRel(startMap), StringComparison.Ordinal);
        Assert.Contains("FLAG_PS_TRAINING_OFF", ReadRel(GameOptionsWriter.Pokemon), StringComparison.Ordinal);
        Assert.Contains("ITEM_INFINITE_CANDY", ReadRel(GameOptionsWriter.PartyMenu), StringComparison.Ordinal);
        Assert.Contains("FLAG_PS_LEVEL_CAP_OFF", ReadRel("src/caps.c"), StringComparison.Ordinal);
        Assert.Contains("DIFFICULTY_EXTRA_HARD", ReadRel("include/constants/difficulty.h"), StringComparison.Ordinal);
        Assert.DoesNotContain("#define B_VAR_DIFFICULTY            0 ", ReadRel("include/config/battle.h"), StringComparison.Ordinal);

        // the chosen flags and variables really were unused
        var header = ReadRel(GameOptionsWriter.Header);
        foreach (var value in System.Text.RegularExpressions.Regex.Matches(header, @"0x[0-9A-F]+").Select(m => m.Value))
        {
            var definedAs = System.Text.RegularExpressions.Regex.Match(ReadRel(value.StartsWith("0x4", StringComparison.Ordinal) && value.Length == 6 ? GameOptionsWriter.VarsFile : GameOptionsWriter.FlagsFile),
                $@"#define\s+(\w+)\s+{value}\b").Groups[1].Value;
            Assert.Matches(@"^(FLAG|VAR)_(UNUSED_)?0x", definedAs);
        }

        // the game reads back with the new item, and saving again changes nothing
        var reloaded = await module.LoadDataAsync(root, tmp.Combine("data"));
        Assert.Contains(reloaded.Items, i => i.Key == "ITEM_INFINITE_CANDY");
        var snapshot = module.ManagedFiles(root).Where(f => File.Exists(Path.Combine([root, .. f.Split('/')]))).ToDictionary(f => f, ReadRel);
        reloaded.GameOptions = Everything;
        await module.SaveDataAsync(reloaded, ctx);
        foreach (var (f, text) in snapshot)
        {
            Assert.True(text == ReadRel(f), $"{f} changed on the second save");
        }
    }

    [Fact]
    public Task Real_emerald_project_gets_the_options_when_available() =>
        RealProject(Environment.GetEnvironmentVariable("POKESTUDIO_EXPANSION_DIR"), "data/maps/InsideOfTruck/scripts.inc");

    [Fact]
    public Task Real_firered_project_gets_the_options_when_available() =>
        RealProject(Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_DIR"), "data/maps/PalletTown_PlayersHouse_2F/scripts.inc");
}

public class GameOptionsCodeTests
{
    internal static readonly GameOptionsSettings All = new()
    {
        Difficulty = OptionMode.PlayerChooses,
        TrainerRulesByDifficulty = true,
        LevelCap = OptionMode.NotUsed,
        Nuzlocke = OptionMode.PlayerChooses,
        AdaptiveSlot = OptionMode.PlayerChooses,
        CenterHelper = new CenterHelperSettings { Enabled = true, NaturePrice = 2000, Ability = OptionMode.PlayerChooses },
        MartSpecialist = true,
    };

    private static async Task RealProject(string? real, string name)
    {
        if (string.IsNullOrEmpty(real) || !Directory.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var root = tmp.Combine("game");
        foreach (var dir in new[] { "include", "src", "data/maps", "data/text", "data/scripts", "data/layouts", "data/tilesets" })
        {
            ProjectService.CopyDirectory(Path.Combine([real, .. dir.Split('/')]), Path.Combine([root, .. dir.Split('/')]));
        }

        foreach (var f in new[] { "Makefile", "charmap.txt", "data/event_scripts.s" })
        {
            File.Copy(Path.Combine([real, .. f.Split('/')]), Path.Combine([root, .. f.Split('/')]));
        }

        var module = new ExpansionModule();
        var data = await module.LoadDataAsync(root, tmp.Combine("data"));
        data.GameOptions = All;
        var ctx = new ModuleSaveContext(new SafeFileWriter(root, allowedFiles: module.ManagedFiles(root)), root, root);
        await module.SaveDataAsync(data, ctx);

        string ReadRel(string rel) => File.ReadAllText(Path.Combine([root, .. rel.Split('/')]));
        var outDir = Path.Combine(AppContext.BaseDirectory, "generated-" + name);
        Directory.CreateDirectory(outDir);
        foreach (var f in new[] { GameOptionsWriter.CodeFile, GameOptionsWriter.CodeHeader, GameOptionsWriter.Script, GameOptionsWriter.Header })
        {
            File.Copy(Path.Combine([root, .. f.Split('/')]), Path.Combine(outDir, Path.GetFileName(f)), true);
        }

        var code = ReadRel(GameOptionsWriter.CodeFile);
        Assert.Contains("[SPECIES_IVYSAUR] = SPECIES_BULBASAUR,", code, StringComparison.Ordinal);
        Assert.Contains("[SPECIES_VENUSAUR] = SPECIES_BULBASAUR,", code, StringComparison.Ordinal);
        Assert.Contains("FLAG_PS_AREA_", code, StringComparison.Ordinal);
        Assert.Contains("if (!IsBagPocketNonEmpty(POCKET_POKE_BALLS))", code, StringComparison.Ordinal); // no balls yet: the area isn't used up
        foreach (System.Text.RegularExpressions.Match m in System.Text.RegularExpressions.Regex.Matches(code, @"FLAG_PS_\w+"))
        {
            Assert.Contains("#define " + m.Value + " ", ReadRel(GameOptionsWriter.Header), StringComparison.Ordinal);
        }

        var setup = ReadRel(GameOptionsWriter.BattleSetup);
        Assert.Contains("PokeStudio_BeforeWildBattle();", setup, StringComparison.Ordinal);
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(setup, @"PokeStudio_AfterBattle\(\);"));
        var battleScripts = ReadRel(GameOptionsWriter.BattleScripts);
        Assert.Contains("FLAG_PS_NUZ_NICK", battleScripts, StringComparison.Ordinal);
        Assert.Contains("the species name doesn't count", battleScripts, StringComparison.Ordinal);
        Assert.Contains("no Revival Blessing in a Nuzlocke", battleScripts, StringComparison.Ordinal);
        Assert.Contains("no revives in a Nuzlocke", ReadRel(GameOptionsWriter.Pokemon), StringComparison.Ordinal);
        Assert.Contains("PokeStudio_UpdateCatchFlag();", ReadRel(GameOptionsWriter.ItemUse), StringComparison.Ordinal);

        // a wiped-out Nuzlocke team can't start battles, and blacking out goes to the wipe handler first
        Assert.Equal(2, System.Text.RegularExpressions.Regex.Count(ReadRel(GameOptionsWriter.WildEncounters), @"if \(!PokeStudio_NuzCanBattle\(\)\)"));
        Assert.Contains("if (!PokeStudio_NuzCanBattle())", ReadRel(GameOptionsWriter.TrainerSee), StringComparison.Ordinal);
        Assert.Contains(GameOptionsWriter.WhiteOutFiles.Where(f => File.Exists(Path.Combine(root, f))).Select(ReadRel),
            t => t.Contains("EventScript_AfterWhiteOutHeal::\n\tcall_if_set FLAG_PS_NUZ_FAINT, PokeStudio_EventScript_NuzWipe", StringComparison.Ordinal));
        Assert.Contains("PokeStudio_EventScript_NuzWipe::", ReadRel(GameOptionsWriter.Script), StringComparison.Ordinal);

        // the helper's ability changer: asked at the start, listed by the game code, the hidden one costs more
        var options = ReadRel(GameOptionsWriter.Script);
        Assert.Contains("PokeStudio_EventScript_AbilityYes::\n\tsetflag FLAG_PS_ABILITY_ON", options, StringComparison.Ordinal);
        Assert.Contains("\tgoto_if_unset FLAG_PS_ABILITY_ON, PokeStudio_EventScript_HelperMenuNoAbility", options, StringComparison.Ordinal);
        Assert.Contains("\tcallnative PokeStudio_ListAbilities", options, StringComparison.Ordinal);
        Assert.Contains("PokeStudio_EventScript_HelperRelearn:\n\tclosemessage\n\trelease\n\tgoto Common_EventScript_MoveRelearner", options, StringComparison.Ordinal);

        // EV spreads: every pair of stats (common ones first) and a reset at the end, the same order in the menu and the code
        var evCode = ReadRel(GameOptionsWriter.CodeFile);
        Assert.Equal(16, System.Text.RegularExpressions.Regex.Count(evCode, @"^\s+\{(\[STAT_\w+\] = \d+, ){2}\[STAT_\w+\] = 4\}, // |^\s+\{0\}, // Reset to 0", System.Text.RegularExpressions.RegexOptions.Multiline));
        Assert.Contains("{[STAT_ATK] = 252, [STAT_SPEED] = 252, [STAT_HP] = 4}, // Attack + Speed", evCode, StringComparison.Ordinal);
        Assert.Contains("{[STAT_DEF] = 252, [STAT_SPDEF] = 252, [STAT_HP] = 4}, // Defense + Sp. Def", evCode, StringComparison.Ordinal);
        Assert.Contains("PokeStudio_Text_Evs0:\n\t.string \"Attack + Speed$\"", options, StringComparison.Ordinal);
        Assert.Contains("PokeStudio_Text_Evs15:\n\t.string \"Reset to 0$\"", options, StringComparison.Ordinal);

        // the Mart Specialist: in every Mart, with stock that grows by badge (FireRed: a staged shop; Emerald: nine lists)
        Assert.Contains("PokeStudio_EventScript_Specialist::\n\tlock\n\tfaceplayer\n\tgoto_if_unset FLAG_SYS_POKEDEX_GET, PokeStudio_EventScript_SpecialistNotYet", options, StringComparison.Ordinal);
        var marts = Directory.GetDirectories(Path.Combine(root, "data", "maps")).Where(d => Path.GetFileName(d).EndsWith("_Mart", StringComparison.Ordinal)).ToList();
        Assert.NotEmpty(marts);
        Assert.True(marts.Count(d => File.ReadAllText(Path.Combine(d, "map.json")).Contains("PokeStudio_EventScript_Specialist", StringComparison.Ordinal)) >= marts.Count - 2);
        if (File.Exists(Path.Combine(root, "src", "data", "shops.json")))
        {
            var shopsText = File.ReadAllText(Path.Combine(root, "src", "data", "shops.json"));
            using var shopsDoc = System.Text.Json.JsonDocument.Parse(shopsText);
            var shop = shopsDoc.RootElement.GetProperty("shops").EnumerateArray().Single(s => s.GetProperty("id").GetString() == "SHOP_ID_PS_SPECIALIST");
            var stages = shop.GetProperty("stages").EnumerateArray().ToList();
            Assert.Equal(9, stages.Count);
            Assert.True(stages[8].GetProperty("items").GetArrayLength() > stages[0].GetProperty("items").GetArrayLength());
            Assert.Contains("\tpokemart SHOP_ID_PS_SPECIALIST", options, StringComparison.Ordinal);
            Assert.Contains("PokeStudio_HasBadge8", ReadRel("src/shop.c"), StringComparison.Ordinal);
        }
        else
        {
            Assert.Contains("PokeStudio_Mart_Specialist8:", options, StringComparison.Ordinal);
            Assert.Contains("\tgoto_if_set FLAG_BADGE08_GET, PokeStudio_EventScript_Specialist8", options, StringComparison.Ordinal);
        }
        Assert.Contains("\tdynmultichoice 0, 0, FALSE, 6, 0, DYN_MULTICHOICE_CB_NONE, gStringVar1, gStringVar2, gStringVar3", options, StringComparison.Ordinal);
        Assert.Contains("\tcheckmoney 20000", options, StringComparison.Ordinal);
        Assert.Contains("#define PS_HIDDEN_ABILITY_BADGE 6", ReadRel(GameOptionsWriter.CodeFile), StringComparison.Ordinal);
        Assert.Contains("#define FLAG_PS_ABILITY_ON ", ReadRel(GameOptionsWriter.Header), StringComparison.Ordinal);

        // the fainting rules start with the Pokédex: losing the first rival battle (Oak's Lab) doesn't end the run
        Assert.Contains("#define PS_NUZ_FAINT_ON (FlagGet(FLAG_PS_NUZ_FAINT) && FlagGet(FLAG_SYS_POKEDEX_GET))", ReadRel(GameOptionsWriter.Header), StringComparison.Ordinal);
        Assert.Contains("PokeStudio_EventScript_NuzWipe::\n\tgoto_if_unset FLAG_SYS_POKEDEX_GET, PokeStudio_EventScript_NuzWipeNotYet", ReadRel(GameOptionsWriter.Script), StringComparison.Ordinal);
        Assert.DoesNotContain("FlagGet(FLAG_PS_NUZ_FAINT)", ReadRel(GameOptionsWriter.Pokemon), StringComparison.Ordinal);
        Assert.DoesNotContain("FlagGet(FLAG_PS_NUZ_FAINT)", battleScripts, StringComparison.Ordinal);

        // trainers by difficulty: Easy skips non-bosses, Hard sees far and in every direction, gym leaders wait for their gym
        var see = ReadRel(GameOptionsWriter.TrainerSee);
        Assert.Contains("PokeStudio_TrainerIgnoresPlayer(", see, StringComparison.Ordinal);
        Assert.True(see.IndexOf("static u8 sPsSeenDirection;", StringComparison.Ordinal) < see.IndexOf("sPsSeenDirection = 0;", StringComparison.Ordinal));
        // Hard sight works out the distance itself (the game's helpers stop at 3 tiles when every object slot is taken)
        Assert.Contains("approachDistance <= max(trainerObj->trainerRange_berryTreeId, psDx == 0 ? 5 : 7)", see, StringComparison.Ordinal);
        Assert.DoesNotContain("sDirectionalApproachDistanceFuncs[i](trainerObj, max(", see, StringComparison.Ordinal);
        // from the side they battle where they stand: walking over could leave them standing in a narrow path
        Assert.Contains("return psDir == trainerObj->facingDirection ? approachDistance : 1;", see, StringComparison.Ordinal);

        // a project patched by an older version gets the new sight on the next save
        var oldBlock = string.Join("\n",
            "    sPsSeenDirection = 0; // Poke Studio: trainers by difficulty (Hard: far, every direction)",
            "    if (PokeStudio_TrainersSeeFar() && trainerObj->trainerType == TRAINER_TYPE_NORMAL) // Poke Studio",
            "    { // Poke Studio",
            "        for (i = 0; i < ARRAY_COUNT(sDirectionalApproachDistanceFuncs); i++) // Poke Studio",
            "        { // Poke Studio",
            "            approachDistance = sDirectionalApproachDistanceFuncs[i](trainerObj, max(trainerObj->trainerRange_berryTreeId, 6), x, y); // Poke Studio",
            "            if (CheckPathBetweenTrainerAndPlayer(trainerObj, approachDistance, i + 1)) // Poke Studio",
            "            { // Poke Studio",
            "                sPsSeenDirection = i + 1; // Poke Studio",
            "                return approachDistance; // Poke Studio",
            "            } // Poke Studio",
            "        } // Poke Studio",
            "        return 0; // Poke Studio",
            "    } // Poke Studio",
            "");
        var newBlock = System.Text.RegularExpressions.Regex.Match(see, @"(?m)^    sPsSeenDirection = 0;[\s\S]*?^    \} // Poke Studio\n").Value;
        File.WriteAllText(Path.Combine(root, "src", "trainer_see.c"), see.Replace(newBlock, oldBlock, StringComparison.Ordinal));
        await module.SaveDataAsync(data, ctx);
        var upgraded = ReadRel(GameOptionsWriter.TrainerSee);
        Assert.Contains("psDx == 0 ? 5 : 7", upgraded, StringComparison.Ordinal);
        Assert.DoesNotContain("max(trainerObj->trainerRange_berryTreeId, 6), x, y)", upgraded, StringComparison.Ordinal);
        var psCode = ReadRel(GameOptionsWriter.CodeFile);
        Assert.Contains("#define PS_TRAINER_RULES TRUE", psCode, StringComparison.Ordinal);
        Assert.Contains("#define PS_GYM_COUNT 8", psCode, StringComparison.Ordinal);
        var gymScripts = Directory.GetDirectories(Path.Combine(root, "data", "maps")).Where(d => Path.GetFileName(d).Contains("_Gym", StringComparison.Ordinal) && !d.EndsWith("_Frlg", StringComparison.Ordinal))
            .Select(d => Path.Combine(d, "scripts.inc")).Where(File.Exists).Count(f => File.ReadAllText(f).Contains("callnative PokeStudio_GymTrainersLeft", StringComparison.Ordinal));
        Assert.Equal(8, gymScripts);
        Assert.Contains("void PokeStudio_NuzAfterWipe(void)", ReadRel(GameOptionsWriter.CodeFile), StringComparison.Ordinal);
        Assert.Contains("IMPORTANT: read each question.", ReadRel(GameOptionsWriter.Script), StringComparison.Ordinal);
        Assert.Contains("POOL_PICK_POKE_STUDIO_ADAPTIVE", ReadRel(GameOptionsWriter.PoolsHeader), StringComparison.Ordinal);
        Assert.Contains("case POOL_PICK_POKE_STUDIO_ADAPTIVE:", ReadRel(GameOptionsWriter.PoolsSource), StringComparison.Ordinal);
        var centers = Directory.GetDirectories(Path.Combine(root, "data", "maps")).Where(d => d.Contains("PokemonCenter_1F", StringComparison.Ordinal)).ToList();
        var withHelper = centers.Count(d => File.ReadAllText(Path.Combine(d, "map.json")).Contains("PokeStudio_EventScript_CenterHelper", StringComparison.Ordinal));
        await File.WriteAllTextAsync(Path.Combine(outDir, "centers.txt"), string.Join("\n", centers.Select(d =>
        {
            var json = File.ReadAllText(Path.Combine(d, "map.json"));
            var at = json.IndexOf("PokeStudio_EventScript_CenterHelper", StringComparison.Ordinal);
            return Path.GetFileName(d) + ": " + (at < 0 ? "none" : json[Math.Max(0, at - 260)..at].Replace("\n", " ", StringComparison.Ordinal));
        })));
        Assert.True(withHelper >= centers.Count - 2, $"{withHelper} of {centers.Count} centers got the helper");

        // saving again changes nothing
        var snapshot = module.ManagedFiles(root).Where(f => File.Exists(Path.Combine([root, .. f.Split('/')]))).ToDictionary(f => f, ReadRel);
        var reloaded = await module.LoadDataAsync(root, tmp.Combine("data"));
        reloaded.GameOptions = All;
        await module.SaveDataAsync(reloaded, ctx);
        foreach (var (f, text) in snapshot)
        {
            var now = ReadRel(f);
            var diff = string.Join(" | ", text.Split('\n').Zip(now.Split('\n')).Where(z => z.First != z.Second).Take(3).Select(z => z.First.Trim() + " => " + z.Second.Trim()));
            Assert.True(text == now, $"{f} changed on the second save: {diff}");
        }
    }

    [Fact]
    public Task Real_emerald_project_gets_the_game_code_when_available() =>
        RealProject(Environment.GetEnvironmentVariable("POKESTUDIO_EXPANSION_DIR"), "emerald");

    [Fact]
    public Task Real_firered_project_gets_the_game_code_when_available() =>
        RealProject(Environment.GetEnvironmentVariable("POKESTUDIO_FIRERED_DIR"), "firered");
}
