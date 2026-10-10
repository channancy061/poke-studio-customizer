using PokeStudio.Core.Changes;
using PokeStudio.Core.Models;
using PokeStudio.Core.Modernize;
using PokeStudio.Core.Modules;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Safety;
using PokeStudio.Core.Validation;
using PokeStudio.Modules.Expansion;

namespace PokeStudio.Core.Tests;

public class ExpansionMovesTests
{
    private const string MovesFile = "src/data/moves_info.h";
    private const string SpeciesFile = "src/data/pokemon/species_info/gen_1_families.h";

    internal static string CopyFixture(TempDir tmp)
    {
        var dest = tmp.Combine("pokeemerald-expansion");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), dest);
        return dest;
    }

    internal static async Task<(ExpansionModule Module, GameData Data, ModuleSaveContext Context)> LoadAsync(string root)
    {
        var module = new ExpansionModule();
        var data = await module.LoadDataAsync(root, Path.Combine(root, "..", "data"));
        var context = new ModuleSaveContext(new SafeFileWriter(root, allowedFiles: module.ManagedFiles(root)), root, root);
        return (module, data, context);
    }

    internal static string Read(string root, string rel) => File.ReadAllText(Path.Combine([root, .. rel.Split('/')]));

    private static List<string> ChangedLines(string before, string after)
    {
        var a = before.Split('\n');
        var b = after.Split('\n');
        return a.Length != b.Length ? ["<line count changed>"] : a.Zip(b).Where(p => p.First != p.Second).Select(p => p.Second.Trim()).ToList();
    }

    private static Move Get(GameData d, string key) => d.Moves.Single(m => m.Key == key);

    private static void Set(GameData d, Move m) => d.Moves[d.Moves.FindIndex(x => x.Key == m.Key)] = m;

    [Fact]
    public async Task Reads_moves_using_the_battle_settings()
    {
        using var tmp = new TempDir();
        var (module, data, _) = await LoadAsync(CopyFixture(tmp));

        Assert.True(module.Capabilities.HasFlag(ModuleCapabilities.Moves));
        Assert.Equal(10, data.Moves.Count); // MOVE_NONE skipped
        var tackle = Get(data, "MOVE_TACKLE");
        Assert.Equal("Tackle", tackle.Name);
        Assert.Equal("Charges the foe with a\nfull-body tackle.", tackle.Description);
        Assert.Equal(40, tackle.Power); // B_UPDATED_MOVE_DATA is latest
        Assert.Equal(100, tackle.Accuracy);
        Assert.True(tackle.MakesContact);
        Assert.Contains("Power", tackle.SettingDependentFields.Keys);
        Assert.Equal(MoveCategory.Status, Get(data, "MOVE_GROWL").Category);
        Assert.Equal("TARGET_BOTH", Get(data, "MOVE_GROWL").Target);
        Assert.Equal(25, Get(data, "MOVE_VINE_LASH").Pp); // #if branch for the current settings
        Assert.Equal("TYPE_FAIRY", Get(data, "MOVE_CHARM").Type);
        Assert.Equal(4, Get(data, "MOVE_PROTECT").Priority);
        Assert.Equal(16, module.Limits.MoveNameLength);
        Assert.Contains(data.Catalog.MoveEffects, e => e.Key == "EFFECT_PROTECT");
        Assert.Equal("Both opponents", data.Catalog.NameOf("TARGET_BOTH"));
    }

    [Fact]
    public async Task Editing_a_setting_dependent_value_changes_only_the_branch_in_use()
    {
        using var tmp = new TempDir();
        var root = CopyFixture(tmp);
        var (module, data, ctx) = await LoadAsync(root);
        var before = Read(root, MovesFile);

        Set(data, Get(data, "MOVE_TACKLE") with { Power = 50 });
        Set(data, Get(data, "MOVE_VINE_LASH") with { Pp = 20 });
        await module.SaveDataAsync(data, ctx);

        Assert.Equal(
            [".power = B_UPDATED_MOVE_DATA >= GEN_7 ? 50 : 35,", ".pp = 20,"],
            ChangedLines(before, Read(root, MovesFile)));
    }

    [Fact]
    public async Task Renames_descriptions_and_flags_are_written_minimally()
    {
        using var tmp = new TempDir();
        var root = CopyFixture(tmp);
        var (module, data, ctx) = await LoadAsync(root);

        Set(data, Get(data, "MOVE_SEED_BOMB") with
        {
            Name = "Seed Barrage",
            Description = "Fires hard seeds that\nhit 2 to 5 times.",
            Category = MoveCategory.Special,
            MakesContact = true,
        });
        Set(data, Get(data, "MOVE_TACKLE") with { MakesContact = false });
        await module.SaveDataAsync(data, ctx);

        var text = Read(root, MovesFile);
        Assert.Contains(".name = COMPOUND_STRING(\"Seed Barrage\"),", text, StringComparison.Ordinal);
        Assert.Contains("\"Fires hard seeds that\\n\"", text, StringComparison.Ordinal);
        Assert.Contains(".category = DAMAGE_CATEGORY_SPECIAL,\n        .makesContact = TRUE,", text, StringComparison.Ordinal);

        var (_, again, _) = await LoadAsync(root);
        Assert.Equal("Seed Barrage", Get(again, "MOVE_SEED_BOMB").Name);
        Assert.True(Get(again, "MOVE_SEED_BOMB").MakesContact);
        Assert.False(Get(again, "MOVE_TACKLE").MakesContact); // the line was removed: FALSE is the default
        Assert.Equal("Seed Barrage", again.Catalog.NameOf("MOVE_SEED_BOMB"));
    }

    [Fact]
    public async Task Saving_without_changes_leaves_moves_untouched()
    {
        using var tmp = new TempDir();
        var root = CopyFixture(tmp);
        var (module, data, ctx) = await LoadAsync(root);
        var before = Read(root, MovesFile);

        await module.SaveDataAsync(data, ctx);

        Assert.Equal(before, Read(root, MovesFile));
    }

    [Fact]
    public async Task Species_types_set_by_a_setting_keep_the_other_branch()
    {
        using var tmp = new TempDir();
        var root = CopyFixture(tmp);
        var (module, data, ctx) = await LoadAsync(root);
        var i = data.Species.FindIndex(s => s.Key == "SPECIES_SPROUTLE");
        var before = Read(root, SpeciesFile);

        data.Species[i] = data.Species[i] with { Types = ["TYPE_GRASS", "TYPE_POISON"] };
        await module.SaveDataAsync(data, ctx);

        Assert.Equal(
            [".types = (P_UPDATED_TYPES >= GEN_6) ? MON_TYPES(TYPE_GRASS, TYPE_FAIRY) : MON_TYPES(TYPE_GRASS, TYPE_POISON),"],
            ChangedLines(before, Read(root, SpeciesFile)));
    }

    [Fact]
    public async Task Move_checks_explain_limits_and_offer_fixes()
    {
        using var tmp = new TempDir();
        var (module, data, _) = await LoadAsync(CopyFixture(tmp));
        Set(data, Get(data, "MOVE_GROWL") with { Power = 30, Name = "Terrifying Growl Max" });
        Set(data, Get(data, "MOVE_TACKLE") with { Accuracy = 120, Priority = 9 });

        var issues = GameValidator.Run(data, module).Where(x => x.Area == "Move").ToList();

        Assert.Contains(issues, x => x.Severity == IssueSeverity.Error && x.Message.Contains("allows 16", StringComparison.Ordinal));
        Assert.Contains(issues, x => x.Severity == IssueSeverity.Warning && x.Message.Contains("status move", StringComparison.Ordinal));
        Assert.Contains(issues, x => x.Message.Contains("accuracy", StringComparison.Ordinal));
        Assert.Contains(issues, x => x.Message.Contains("priority 9", StringComparison.Ordinal));

        var undo = new UndoRedoStack();
        undo.Execute(issues.First(x => x.Message.Contains("allows 16", StringComparison.Ordinal)).Fix!(data));
        Assert.Equal("Terrifying Growl", Get(data, "MOVE_GROWL").Name);
        Assert.Equal("Terrifying Growl", data.Catalog.NameOf("MOVE_GROWL"));
    }
}

public class ModernizeTests
{
    [Fact]
    public async Task Offers_each_generation_the_project_ships()
    {
        using var tmp = new TempDir();
        var (module, data, _) = await ExpansionMovesTests.LoadAsync(ExpansionMovesTests.CopyFixture(tmp));

        var gens = module.ModernizeGenerations(data);

        Assert.Equal([9, 3], gens.Select(g => g.Generation));
        Assert.True(gens[0].IsLatest);
        Assert.Contains("Scarlet & Violet", gens[0].Label, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Plans_level_up_changes_against_another_generation_and_applies_them_as_one_undo_step()
    {
        using var tmp = new TempDir();
        var root = ExpansionMovesTests.CopyFixture(tmp);
        var (module, data, ctx) = await ExpansionMovesTests.LoadAsync(root);

        var reference = await module.LoadModernizeReferenceAsync(data, 3);
        Assert.NotNull(reference);
        var plan = Modernizer.Plan(data, reference, moves: true, species: false);

        // Sproutle and its Frost form share one list: it appears once. Bloomtle is identical in both files.
        var sproutle = Assert.Single(plan, r => r.SpeciesKey == "SPECIES_SPROUTLE");
        Assert.Equal(["SPECIES_SPROUTLE_FROST"], sproutle.SharedWith);
        Assert.Equal(["MOVE_SLEEP_DUST"], sproutle.MovesRemoved);
        Assert.DoesNotContain(plan, r => r.SpeciesKey is "SPECIES_BLOOMTLE" or "SPECIES_SPROUTLE_FROST");
        var ember = Assert.Single(plan, r => r.SpeciesKey == "SPECIES_EMBERPUP");
        Assert.Equal(["MOVE_PROTECT"], ember.MovesAdded);

        var undo = new UndoRedoStack();
        undo.Execute(Modernizer.ToChange(data, plan, "Gen 3"));
        Assert.Equal(1, undo.UnsavedCount);
        Assert.Equal(3, data.LearnsetFor(data.Species.Single(s => s.Key == "SPECIES_EMBERPUP"))!.LevelUp.Count);

        await module.SaveDataAsync(data, ctx);
        var text = ExpansionMovesTests.Read(root, "src/data/pokemon/level_up_learnsets/gen_9.h");
        Assert.Contains("LEVEL_UP_MOVE(15, MOVE_PROTECT),", text, StringComparison.Ordinal);

        undo.Undo();
        Assert.Equal(2, data.LearnsetFor(data.Species.Single(s => s.Key == "SPECIES_EMBERPUP"))!.LevelUp.Count);
    }

    [Fact]
    public async Task Latest_generation_with_latest_settings_has_nothing_to_change_for_moves()
    {
        using var tmp = new TempDir();
        var (module, data, _) = await ExpansionMovesTests.LoadAsync(ExpansionMovesTests.CopyFixture(tmp));

        var reference = await module.LoadModernizeReferenceAsync(data, 9);

        Assert.Empty(Modernizer.Plan(data, reference!, moves: true, species: false));
    }

    [Fact]
    public async Task Stats_types_and_abilities_follow_the_chosen_generation()
    {
        using var tmp = new TempDir();
        var (module, data, _) = await ExpansionMovesTests.LoadAsync(ExpansionMovesTests.CopyFixture(tmp));

        // The fixture sets P_UPDATED_TYPES to GEN_3, so Sproutle is Grass only; Gen 9 makes it Grass/Fairy.
        var plan = Modernizer.Plan(data, (await module.LoadModernizeReferenceAsync(data, 9))!, moves: false, species: true);

        var row = Assert.Single(plan);
        Assert.Equal("SPECIES_SPROUTLE", row.SpeciesKey);
        Assert.Equal(["type Grass → Grass/Fairy"], row.SpeciesChanges);

        new UndoRedoStack().Execute(Modernizer.ToChange(data, plan, "Gen 9"));
        Assert.Equal(["TYPE_GRASS", "TYPE_FAIRY"], data.Species.Single(s => s.Key == "SPECIES_SPROUTLE").Types);
    }
}
