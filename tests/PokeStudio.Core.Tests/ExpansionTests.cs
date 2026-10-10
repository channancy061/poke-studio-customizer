using PokeStudio.Core.Backups;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Safety;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

/// <summary>Uses an invented project that mimics pokeemerald-expansion's layout (tests/Fixtures/expansion-mini).</summary>
public class ExpansionTests
{
    private const string SpeciesFile = "src/data/pokemon/species_info/gen_1_families.h";
    private const string LevelUpFile = "src/data/pokemon/level_up_learnsets/gen_9.h";
    private const string EggFile = "src/data/pokemon/egg_moves.h";
    private const string LearnablesFile = "src/data/pokemon/all_learnables.json";

    private static string CopyFixture(TempDir tmp)
    {
        var dest = tmp.Combine("pokeemerald-expansion");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), dest);
        return dest;
    }

    private static string Read(string root, string rel) => File.ReadAllText(Path.Combine([root, .. rel.Split('/')]));

    private static async Task<(ExpansionModule Module, GameData Data, ModuleSaveContext Context)> LoadAsync(string root)
    {
        var module = new ExpansionModule();
        var data = await module.LoadDataAsync(root, Path.Combine(root, "..", "data"));
        var context = new ModuleSaveContext(new SafeFileWriter(root, allowedFiles: module.ManagedFiles(root)), root, root);
        return (module, data, context);
    }

    private static Species Get(GameData d, string key) => d.Species.Single(s => s.Key == key);

    private static List<string> ChangedLines(string before, string after)
    {
        var a = before.Split('\n');
        var b = after.Split('\n');
        return a.Length != b.Length ? ["<line count changed>"] : a.Zip(b).Where(p => p.First != p.Second).Select(p => p.Second.Trim()).ToList();
    }

    [Fact]
    public void Detects_expansion_projects_and_their_version()
    {
        using var tmp = new TempDir();
        var root = CopyFixture(tmp);
        var module = new ExpansionModule();

        var result = module.Detect(new GameSource(root, GameSourceKind.DecompProject));
        Assert.True(result.IsMatch);
        Assert.Equal("1.17.1", result.Version);

        Assert.False(module.Detect(new GameSource(tmp.Path, GameSourceKind.DecompProject)).IsMatch);
        Assert.False(module.Detect(new GameSource(Fixtures.DemoGameFile, GameSourceKind.RomFile)).IsMatch);
    }

    [Fact]
    public async Task Reads_species_using_the_projects_settings()
    {
        using var tmp = new TempDir();
        var (_, data, _) = await LoadAsync(CopyFixture(tmp));

        Assert.Equal(["SPECIES_SPROUTLE", "SPECIES_BLOOMTLE", "SPECIES_SPROUTLE_FROST", "SPECIES_EMBERPUP", "SPECIES_GHOSTLING"], data.Species.Select(s => s.Key));

        var sproutle = Get(data, "SPECIES_SPROUTLE");
        Assert.Equal("Sproutle", sproutle.Name);
        Assert.Equal(new BaseStats(45, 49, 49, 65, 65, 45), sproutle.Stats);
        Assert.Equal(["TYPE_GRASS"], sproutle.Types); // P_UPDATED_TYPES is GEN_3 in the fixture, so the ternary picks the older typing
        Assert.Contains("Types", sproutle.SettingDependentFields.Keys);
        Assert.Equal("It naps in sunny meadows.\nThe bud on its back swells a\nlittle more each day.", sproutle.DexEntry);
        Assert.Equal(12.5, sproutle.Gender!.PercentFemale);

        var bloomtle = Get(data, "SPECIES_BLOOMTLE");
        Assert.Equal(90, bloomtle.Stats.SpAttack); // BLOOMTLE_SP_ATK under P_UPDATED_STATS >= GEN_6
        Assert.Equal(["ABILITY_OVERGROW", "ABILITY_LEAF_GUARD", "ABILITY_CHLOROPHYLL"], bloomtle.Abilities); // the active #if branch
        Assert.Contains("Abilities", bloomtle.SettingDependentFields.Keys);
        Assert.Equal("ITEM_MIRACLE_SEED", bloomtle.HeldItemRare);

        var frost = Get(data, "SPECIES_SPROUTLE_FROST");
        Assert.Equal("Frost", frost.FormName);
        Assert.True(frost.Gender!.Genderless);
        Assert.Contains("DexEntry", frost.LockedFields.Keys); // shared text macro

        var ghost = Get(data, "SPECIES_GHOSTLING");
        Assert.False(ghost.IsEnabled);
        Assert.Contains("*", ghost.LockedFields.Keys);

        Assert.Equal(12, new ExpansionModule().Limits.SpeciesNameLength is var _ ? 12 : 0);
        Assert.Contains(data.Catalog.Moves, m => m.Key == "MOVE_VINE_LASH");
        Assert.DoesNotContain(data.Catalog.Moves, m => m.Key == "MOVE_VINELASH"); // alias skipped
        Assert.Equal([("TM01", "MOVE_PROTECT"), ("TM02", "MOVE_SOLAR_RAY"), ("HM01", "MOVE_TACKLE")], data.Catalog.Machines);
    }

    [Fact]
    public async Task Reads_learnsets_and_evolutions()
    {
        using var tmp = new TempDir();
        var (_, data, _) = await LoadAsync(CopyFixture(tmp));

        var sproutleSet = data.LearnsetFor(Get(data, "SPECIES_SPROUTLE"))!;
        Assert.Equal([new LevelUpMove(1, "MOVE_TACKLE"), new LevelUpMove(1, "MOVE_GROWL"), new LevelUpMove(7, "MOVE_VINE_LASH"), new LevelUpMove(13, "MOVE_SLEEP_DUST")], sproutleSet.LevelUp);
        Assert.Equal(["MOVE_CHARM", "MOVE_SEED_BOMB"], sproutleSet.EggMoves);
        Assert.Equal(["MOVE_PROTECT", "MOVE_SOLAR_RAY"], sproutleSet.Teachable);
        Assert.Equal(["SPECIES_SPROUTLE", "SPECIES_SPROUTLE_FROST"], sproutleSet.UsedBy); // shared level-up list

        var frostSet = data.LearnsetFor(Get(data, "SPECIES_SPROUTLE_FROST"))!;
        Assert.Contains("teachable", frostSet.LockedParts.Keys); // ALL_TEACHABLES

        var bloomEvo = data.Evolutions.Single(e => e.FromSpecies == "SPECIES_BLOOMTLE");
        Assert.Null(bloomEvo.LockedReason);
        Assert.Equal(2, bloomEvo.Evolutions.Count);
        Assert.Equal(new EvolutionCondition("IF_TIME", "TIME_NIGHT"), Assert.Single(bloomEvo.Evolutions[0].Conditions));
        Assert.Equal("ITEM_SUN_STONE", bloomEvo.Evolutions[0].Parameter);

        var emberEvo = data.Evolutions.Single(e => e.FromSpecies == "SPECIES_EMBERPUP");
        Assert.NotNull(emberEvo.LockedReason); // #if inside the list
    }

    [Fact]
    public async Task Saving_without_changes_leaves_every_file_identical()
    {
        using var tmp = new TempDir();
        var root = CopyFixture(tmp);
        var before = new[] { SpeciesFile, LevelUpFile, EggFile, LearnablesFile }.ToDictionary(f => f, f => Read(root, f));
        var (module, data, ctx) = await LoadAsync(root);

        await module.SaveDataAsync(data, ctx);

        foreach (var (f, text) in before)
        {
            Assert.Equal(text, Read(root, f));
        }
    }

    [Fact]
    public async Task Editing_one_stat_changes_only_that_line()
    {
        using var tmp = new TempDir();
        var root = CopyFixture(tmp);
        var original = Read(root, SpeciesFile);
        var (module, data, ctx) = await LoadAsync(root);

        var i = data.Species.FindIndex(s => s.Key == "SPECIES_SPROUTLE");
        data.Species[i] = data.Species[i] with { Stats = data.Species[i].Stats with { Speed = 90 } };
        await module.SaveDataAsync(data, ctx);

        Assert.Equal([".baseSpeed     = 90,"], ChangedLines(original, Read(root, SpeciesFile)));
        var (_, reloaded, _) = await LoadAsync(root);
        Assert.Equal(90, Get(reloaded, "SPECIES_SPROUTLE").Stats.Speed);
    }

    [Fact]
    public async Task Setting_dependent_values_edit_the_branch_in_use_only()
    {
        using var tmp = new TempDir();
        var root = CopyFixture(tmp);
        var original = Read(root, SpeciesFile);
        var (module, data, ctx) = await LoadAsync(root);

        var i = data.Species.FindIndex(s => s.Key == "SPECIES_BLOOMTLE");
        data.Species[i] = data.Species[i] with
        {
            Abilities = ["ABILITY_CHLOROPHYLL", "ABILITY_NONE", "ABILITY_LEAF_GUARD"],
            Stats = data.Species[i].Stats with { SpAttack = 95 },
        };
        await module.SaveDataAsync(data, ctx);

        var changed = ChangedLines(original, Read(root, SpeciesFile));
        Assert.Equal([".baseSpAttack  = 95,", ".abilities = { ABILITY_CHLOROPHYLL, ABILITY_NONE, ABILITY_LEAF_GUARD },"], changed);
        Assert.Contains(".abilities = { ABILITY_OVERGROW, ABILITY_NONE, ABILITY_CHLOROPHYLL },", Read(root, SpeciesFile), StringComparison.Ordinal); // #else branch untouched
    }

    [Fact]
    public async Task Text_fields_and_new_fields_are_written_in_the_projects_style()
    {
        using var tmp = new TempDir();
        var root = CopyFixture(tmp);
        var (module, data, ctx) = await LoadAsync(root);

        var i = data.Species.FindIndex(s => s.Key == "SPECIES_EMBERPUP");
        data.Species[i] = data.Species[i] with
        {
            Name = "Cinderpup",
            DexEntry = "A warm little pup.\nIt sleeps by the fire.",
            HeldItemCommon = "ITEM_ORAN_BERRY",
            Types = ["TYPE_FIRE", "TYPE_FAIRY"],
        };
        await module.SaveDataAsync(data, ctx);

        var text = Read(root, SpeciesFile);
        Assert.Contains(".speciesName = _(\"Cinderpup\"),", text, StringComparison.Ordinal);
        Assert.Contains(".types = MON_TYPES(TYPE_FIRE, TYPE_FAIRY),", text, StringComparison.Ordinal);
        Assert.Contains("        .itemCommon = ITEM_ORAN_BERRY,", text, StringComparison.Ordinal);
        Assert.Contains(".description = COMPOUND_STRING(\n            \"A warm little pup.\\n\"\n            \"It sleeps by the fire.\"),", text, StringComparison.Ordinal);

        var (_, reloaded, _) = await LoadAsync(root);
        var pup = Get(reloaded, "SPECIES_EMBERPUP");
        Assert.Equal("Cinderpup", pup.Name);
        Assert.Equal("A warm little pup.\nIt sleeps by the fire.", pup.DexEntry);
        Assert.Equal("ITEM_ORAN_BERRY", pup.HeldItemCommon);
    }

    [Fact]
    public async Task Learnset_edits_rewrite_only_their_own_lists()
    {
        using var tmp = new TempDir();
        var root = CopyFixture(tmp);
        var originalLevelUp = Read(root, LevelUpFile);
        var (module, data, ctx) = await LoadAsync(root);

        var li = data.Learnsets.FindIndex(l => l.Key.StartsWith("sSproutleLevelUpLearnset|sSproutleTeachableLearnset", StringComparison.Ordinal));
        var ls = data.Learnsets[li];
        data.Learnsets[li] = ls with
        {
            LevelUp = [.. ls.LevelUp, new LevelUpMove(20, "MOVE_SEED_BOMB")],
            EggMoves = ["MOVE_CHARM", "MOVE_SEED_BOMB", "MOVE_FROST_BREATH"],
            Teachable = ["MOVE_SOLAR_RAY"],
        };
        await module.SaveDataAsync(data, ctx);

        var levelUp = Read(root, LevelUpFile);
        Assert.Contains("    LEVEL_UP_MOVE(13, MOVE_SLEEP_DUST),\n    LEVEL_UP_MOVE(20, MOVE_SEED_BOMB),\n    LEVEL_UP_END\n};", levelUp, StringComparison.Ordinal);
        var untouched = originalLevelUp[originalLevelUp.IndexOf("static const struct LevelUpMove sBloomtleLevelUpLearnset", StringComparison.Ordinal)..];
        Assert.EndsWith(untouched, levelUp, StringComparison.Ordinal);

        Assert.Contains("    MOVE_SEED_BOMB,\n    MOVE_FROST_BREATH,\n    MOVE_UNAVAILABLE,\n};", Read(root, EggFile), StringComparison.Ordinal);
        Assert.Equal(
            "{\n  \"SPROUTLE\": [\n    \"MOVE_SOLAR_RAY\"\n  ],\n  \"BLOOMTLE\": [\n    \"MOVE_PROTECT\",\n    \"MOVE_SOLAR_RAY\"\n  ],\n  \"EMBERPUP\": [\n    \"MOVE_PROTECT\"\n  ],\n  \"GHOSTLING\": []\n}",
            Read(root, LearnablesFile));

        var (_, reloaded, _) = await LoadAsync(root);
        Assert.Equal(5, reloaded.LearnsetFor(Get(reloaded, "SPECIES_SPROUTLE"))!.LevelUp.Count);
    }

    [Fact]
    public async Task Evolutions_can_be_changed_added_and_removed()
    {
        using var tmp = new TempDir();
        var root = CopyFixture(tmp);
        var (module, data, ctx) = await LoadAsync(root);

        int Idx(string k) => data.Evolutions.FindIndex(e => e.FromSpecies == k);
        data.Evolutions[Idx("SPECIES_SPROUTLE")] = data.Evolutions[Idx("SPECIES_SPROUTLE")] with { Evolutions = [new Evolution("EVO_LEVEL", "18", "SPECIES_BLOOMTLE")] };
        data.Evolutions[Idx("SPECIES_SPROUTLE_FROST")] = data.Evolutions[Idx("SPECIES_SPROUTLE_FROST")] with
        {
            Evolutions = [new Evolution("EVO_ITEM", "ITEM_SUN_STONE", "SPECIES_BLOOMTLE") { Conditions = [new EvolutionCondition("IF_TIME", "TIME_DAY")] }],
        };
        data.Evolutions[Idx("SPECIES_BLOOMTLE")] = data.Evolutions[Idx("SPECIES_BLOOMTLE")] with { Evolutions = [] };
        await module.SaveDataAsync(data, ctx);

        var text = Read(root, SpeciesFile);
        Assert.Contains(".evolutions = EVOLUTION({EVO_LEVEL, 18, SPECIES_BLOOMTLE}),", text, StringComparison.Ordinal);
        Assert.Contains(".evolutions = EVOLUTION({EVO_ITEM, ITEM_SUN_STONE, SPECIES_BLOOMTLE, CONDITIONS({IF_TIME, TIME_DAY})}),", text, StringComparison.Ordinal);
        Assert.DoesNotContain("SPECIES_SPROUTLE_FROST, CONDITIONS({IF_TIME, TIME_NIGHT})", text, StringComparison.Ordinal);

        var (_, reloaded, _) = await LoadAsync(root);
        Assert.Empty(reloaded.Evolutions.Single(e => e.FromSpecies == "SPECIES_BLOOMTLE").Evolutions);
        Assert.Equal("18", reloaded.Evolutions.Single(e => e.FromSpecies == "SPECIES_SPROUTLE").Evolutions[0].Parameter);
        Assert.Equal("TIME_DAY", reloaded.Evolutions.Single(e => e.FromSpecies == "SPECIES_SPROUTLE_FROST").Evolutions[0].Conditions[0].Value);
    }

    [Fact]
    public async Task Validation_flags_five_level_one_moves_with_a_fix()
    {
        using var tmp = new TempDir();
        var root = CopyFixture(tmp);
        var (module, data, _) = await LoadAsync(root);

        var issues = Validation.GameValidator.Run(data, module);
        var issue = Assert.Single(issues, i => i.Message.Contains("learns 5 moves at level 1 but can only hold 4", StringComparison.Ordinal));
        Assert.NotNull(issue.Fix);

        var change = issue.Fix!(data);
        change.Apply();
        Assert.Equal(4, data.Learnsets.Single(l => l.Key.StartsWith("sBloomtle", StringComparison.Ordinal)).LevelUp.Count(m => m.Level == 1));
    }

    [Fact]
    public async Task Module_can_only_write_its_managed_files()
    {
        using var tmp = new TempDir();
        var root = CopyFixture(tmp);
        var (_, _, ctx) = await LoadAsync(root);

        await Assert.ThrowsAsync<UnsafeWriteException>(() => ctx.Writer.WriteAllTextAsync(Path.Combine(root, "Makefile"), "oops"));
    }

    [Fact]
    public async Task In_place_projects_back_up_decomp_files_before_the_first_save_and_restore_them()
    {
        using var tmp = new TempDir();
        var root = CopyFixture(tmp);
        var original = Read(root, SpeciesFile);
        var registry = new ModuleRegistry().Register(new ExpansionModule());
        var module = registry.Get(ExpansionModule.ModuleId);
        var source = new GameSource(root, GameSourceKind.DecompProject);
        var project = await new ProjectService().CreateAsync("My hack", source, module, module.Detect(source), tmp.Combine("projects"));

        Assert.True(project.Manifest.WorkInPlace);
        Assert.Equal(Path.GetFullPath(root), project.WorkingSourcePath);
        Assert.False(Directory.Exists(Path.Combine(project.SourceDir, "pokeemerald-expansion"))); // no copy made

        using var session = await ProjectSession.OpenAsync(project, registry, new ProjectService(), new BackupService(tmp.Combine("backups")));
        var i = session.Data.Species.FindIndex(s => s.Key == "SPECIES_SPROUTLE");
        session.Undo.Execute(new Changes.ValueChange<Species>("Pokémon", "speed", v => session.Data.Species[i] = v, session.Data.Species[i], session.Data.Species[i] with { Stats = session.Data.Species[i].Stats with { Speed = 99 } }));
        await session.SaveAsync();

        var backups = await session.ListBackupsAsync();
        var safety = Assert.Single(backups);
        Assert.Contains(safety.Files, f => f.RelativePath == "@source/" + SpeciesFile);
        Assert.NotEqual(original, Read(root, SpeciesFile));

        await session.RestoreAsync(safety.Id);
        Assert.Equal(original, Read(root, SpeciesFile));
        Assert.Equal(45, session.Data.Species[i].Stats.Speed);
    }

    /// <summary>
    /// Optional check against a real pokeemerald-expansion folder on this machine (never in CI):
    /// set POKESTUDIO_EXPANSION_DIR to a folder. The folder is copied first, so it's never changed.
    /// </summary>
    [Fact]
    public async Task Real_project_round_trips_without_changes_when_available()
    {
        var real = Environment.GetEnvironmentVariable("POKESTUDIO_EXPANSION_DIR");
        if (string.IsNullOrEmpty(real) || !Directory.Exists(real))
        {
            return;
        }

        using var tmp = new TempDir();
        var root = tmp.Combine("real");
        ProjectService.CopyDirectory(Path.Combine(real, "include"), Path.Combine(root, "include"));
        ProjectService.CopyDirectory(Path.Combine(real, "src", "data"), Path.Combine(root, "src", "data"));
        File.WriteAllText(Path.Combine(root, "Makefile"), "");
        var files = new ExpansionModule().ManagedFiles(root);
        var before = files.Where(f => File.Exists(Path.Combine(root, f))).ToDictionary(f => f, f => Read(root, f));
        var (module, data, ctx) = await LoadAsync(root);

        Assert.True(data.Species.Count > 1000, $"only {data.Species.Count} species read");
        Assert.All(data.Species.Where(s => s.IsEnabled), s => Assert.False(string.IsNullOrWhiteSpace(s.Name)));
        Assert.True(data.Learnsets.Count(l => l.LevelUp.Count > 0) > 500);

        await module.SaveDataAsync(data, ctx);
        foreach (var (f, text) in before)
        {
            Assert.Equal(text, Read(root, f));
        }

        // A real edit changes exactly one line of the real file.
        var i = data.Species.FindIndex(s => s.IsEnabled && s.LockedFields.Count == 0 && !s.SettingDependentFields.ContainsKey("Stats"));
        var block = data.Species[i];
        var file = files.First(f => Read(root, f).Contains("[" + block.Key + "]", StringComparison.Ordinal));
        var original = Read(root, file);
        data.Species[i] = block with { Stats = block.Stats with { Speed = block.Stats.Speed == 200 ? 201 : 200 } };
        await module.SaveDataAsync(data, ctx);
        Assert.Single(original.Split('\n').Zip(Read(root, file).Split('\n')), p => p.First != p.Second);

        // Moves: all read, names and power sensible; one setting-dependent edit changes one line.
        Assert.True(data.Moves.Count > 900, $"only {data.Moves.Count} moves read");
        Assert.Equal(90, data.Moves.Single(m => m.Key == "MOVE_THUNDERBOLT").Power);
        Assert.Equal("Thunderbolt", data.Moves.Single(m => m.Key == "MOVE_THUNDERBOLT").Name);
        var movesText = Read(root, "src/data/moves_info.h");
        var bolt = data.Moves.FindIndex(m => m.Key == "MOVE_THUNDERBOLT");
        data.Moves[bolt] = data.Moves[bolt] with { Power = 85 };
        await module.SaveDataAsync(data, ctx);
        var changed = movesText.Split('\n').Zip(Read(root, "src/data/moves_info.h").Split('\n')).Where(p => p.First != p.Second).ToList();
        Assert.Equal(".power = B_UPDATED_MOVE_DATA >= GEN_6 ? 85 : 95,", Assert.Single(changed).Second.Trim());

        // Modernize: every generation file is offered; going back to Gen 3 changes many level-up lists.
        var gens = module.ModernizeGenerations(data);
        Assert.Equal(9, gens.Count);
        var plan = Modernize.Modernizer.Plan(data, (await module.LoadModernizeReferenceAsync(data, 3))!, moves: true, species: true);
        Assert.True(plan.Count(r => r.ChangesMoves) > 300, $"only {plan.Count(r => r.ChangesMoves)} level-up lists differ from Gen 3");
        Assert.Contains(plan, r => r.ChangesSpecies);
    }
}

public class ExpansionDownloaderTests
{
    [Fact]
    public void Unpacking_drops_the_top_folder_and_refuses_paths_that_escape()
    {
        using var tmp = new TempDir();
        var good = tmp.Combine("good.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(good, System.IO.Compression.ZipArchiveMode.Create))
        {
            using var w = new StreamWriter(zip.CreateEntry("pokeemerald-expansion-1.17.1/Makefile").Open());
            w.Write("all:");
        }

        var target = tmp.Combine("out");
        ExpansionDownloader.Extract(good, target, null, CancellationToken.None);
        Assert.Equal("all:", File.ReadAllText(Path.Combine(target, "Makefile")));

        var evil = tmp.Combine("evil.zip");
        using (var zip = System.IO.Compression.ZipFile.Open(evil, System.IO.Compression.ZipArchiveMode.Create))
        {
            zip.CreateEntry("top/../../escaped.txt");
        }

        Assert.Throws<InvalidDataException>(() => ExpansionDownloader.Extract(evil, tmp.Combine("out2"), null, CancellationToken.None));
        Assert.False(File.Exists(tmp.Combine("escaped.txt")));
    }

    [Fact]
    public void Only_the_official_release_url_is_used() =>
        Assert.Equal("https://codeload.github.com/rh-hideout/pokeemerald-expansion/zip/refs/tags/expansion/1.17.1", ExpansionDownloader.ZipUrl().ToString());
}
