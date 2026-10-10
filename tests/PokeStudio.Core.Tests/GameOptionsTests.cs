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
        LevelCap = OptionMode.NotUsed,
        Nuzlocke = OptionMode.PlayerChooses,
        AdaptiveSlot = OptionMode.PlayerChooses,
        CenterHelper = new CenterHelperSettings { Enabled = true, NaturePrice = 2000 },
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
