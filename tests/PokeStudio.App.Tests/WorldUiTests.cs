using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;
using PokeStudio.Core.Models;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Settings;

namespace PokeStudio.App.Tests;

public class WorldUiTests
{
    private static async Task<(ShellViewModel Shell, string Root)> OpenFixtureAsync(TestEnvironment env)
    {
        var root = Path.Combine(env.Root, "my-hack");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), root);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(root));
        return (Assert.IsType<ShellViewModel>(env.Main.CurrentView), root);
    }

    private static T Open<T>(ShellViewModel shell, NavKey key)
    {
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == key);
        return Assert.IsType<T>(shell.CurrentPage);
    }

    private static async Task SaveAsync(ShellViewModel shell)
    {
        await shell.SaveCommand.ExecuteAsync(null);
        if (shell.IsValidationOpen)
        {
            Assert.False(shell.HasErrors, string.Join(" | ", shell.Issues.Where(i => i.IsError).Select(i => i.Text)));
            await shell.SaveAnywayCommand.ExecuteAsync(null);
        }
    }

    [AvaloniaFact]
    public async Task Wild_encounters_show_chances_and_edit_slots()
    {
        using var env = new TestEnvironment();
        var (shell, root) = await OpenFixtureAsync(env);
        var page = Open<EncountersPageViewModel>(shell, NavKey.Encounters);

        Assert.Equal(["Meadow Route", "Frost Lake"], page.Items.Select(i => i.Title));
        page.Search = "emberpup";
        Assert.Equal(2, page.Items.Count);
        page.Search = "";
        page.SelectedItem = page.Items.Single(i => i.Title == "Frost Lake");
        var fishing = page.Editor!.Methods.Single(m => m.Title == "Fishing");
        Assert.Equal("70% · Old Rod", fishing.Slots[0].Chance); // 70 of the old rod's 100
        Assert.Equal("60% · Good Rod", fishing.Slots[2].Chance);

        var slot = page.Editor.Methods[0].Slots[0];
        slot.MinLevel = 20; // also raises the max so the range stays valid
        Assert.Equal((20, 20), (shell.Session.Data.Encounters[1].Methods[0].Slots[0].MinLevel, shell.Session.Data.Encounters[1].Methods[0].Slots[0].MaxLevel));

        await SaveAsync(shell);
        Assert.Contains("\"min_level\": 20", File.ReadAllText(Path.Combine(root, "src", "data", "wild_encounters.json")), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Wild_tools_change_levels_and_replace_everywhere()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        var page = Open<EncountersPageViewModel>(shell, NavKey.Encounters);

        page.ToggleToolsCommand.Execute(null);
        page.AllAreas = true;
        page.LevelChange = 10;
        page.ApplyLevelChangeCommand.Execute(null);
        Assert.Equal(13, shell.Session.Data.Encounters[0].Methods[0].Slots[0].MinLevel);

        page.ReplaceFrom = page.SpeciesOptions.Single(s => s.Key == "SPECIES_EMBERPUP");
        page.ReplaceTo = page.SpeciesOptions.Single(s => s.Key == "SPECIES_BLOOMTLE");
        page.ApplyReplaceCommand.Execute(null);
        Assert.DoesNotContain(shell.Session.Data.Encounters.SelectMany(e => e.Methods).SelectMany(m => m.Slots), s => s.Species == "SPECIES_EMBERPUP");
        Assert.Equal(2, shell.Changes.Count);
    }

    [AvaloniaFact]
    public async Task Items_and_shops_edit_with_presets_badge_tiers_and_cheap_candies()
    {
        using var env = new TestEnvironment();
        var (shell, root) = await OpenFixtureAsync(env);
        var page = Open<ItemsPageViewModel>(shell, NavKey.Items);

        page.SelectedItem = page.Items.Single(i => i.Key == "ITEM_MIRACLE_SEED");
        var item = page.ItemEditor!;
        item.Price = 3000;
        item.NameDraft = "Wonder Seed";
        Assert.Equal("Wonder Seed", shell.Session.Data.Catalog.NameOf("ITEM_MIRACLE_SEED"));
        Assert.Contains("Not sold in any shop", item.Usage, StringComparison.Ordinal);

        page.ShowShops = true;
        var mart = page.MartEditor!;
        mart.NewItem = mart.ItemOptions.Single(i => i.Key == "ITEM_MIRACLE_SEED");
        mart.AddItemCommand.Execute(null);
        Assert.Equal(3, mart.Stock.Count);
        mart.TierItem = mart.ItemOptions.Single(i => i.Key == "ITEM_SUN_STONE");
        mart.TierBadge = 2;
        mart.AddToTierCommand.Execute(null); // already sold from the start, so nothing new — but it's allowed
        mart.Preset = mart.Presets.Single(p => p.Id == "berries");
        mart.PresetBadge = 3;
        mart.AddPresetCommand.Execute(null);
        Assert.Contains(mart.Tiers, t => t.Badge == 2);

        page.CandyPrice = 0;
        page.CheapCandiesCommand.Execute(null);
        Assert.Equal("This game has no Rare Candy item.", shell.Toast); // the invented fixture has no candy

        await SaveAsync(shell);
        var script = File.ReadAllText(Path.Combine(root, "data", "maps", "MeadowTown_Mart", "scripts.inc"));
        Assert.Contains("\t.2byte ITEM_MIRACLE_SEED\n\tpokemartlistend", script, StringComparison.Ordinal);
        Assert.Contains("MeadowTown_Mart_Pokemart_PokeStudioTier2::", script, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Text_editor_shows_boxes_and_flags_lines_that_are_too_wide()
    {
        using var env = new TestEnvironment();
        var (shell, root) = await OpenFixtureAsync(env);
        var page = Open<TextPageViewModel>(shell, NavKey.Text);

        page.Search = "meadow";
        page.SelectedItem = page.Items.Single(i => i.Key == "MeadowTown_Mart_Text_Welcome");
        var editor = page.Editor!;
        Assert.Equal("Welcome to the meadow!\nWe sell berries and stones.\n\nHave a nice day!", editor.Draft);
        Assert.Equal(3, editor.Preview.Count);
        Assert.True(editor.Preview[2].NewBox);
        Assert.False(editor.HasTooWide);

        editor.Draft = "Welcome to the meadow, where the flowers grow tall and the berries are sweet!";
        Assert.True(editor.HasTooWide);
        editor.Draft = "Welcome to the meadow!\nThe berries are sweet.";
        Assert.False(editor.HasTooWide);
        Assert.Single(shell.Changes); // typing merges into one change

        await SaveAsync(shell);
        Assert.Contains("\t.string \"Welcome to the meadow!\\n\"\n\t.string \"The berries are sweet.$\"",
            File.ReadAllText(Path.Combine(root, "data", "maps", "MeadowTown_Mart", "scripts.inc")), StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Level_curve_moves_the_whole_game_and_sets_caps()
    {
        using var env = new TestEnvironment();
        var (shell, root) = await OpenFixtureAsync(env);
        var page = Open<ProgressionPageViewModel>(shell, NavKey.Progression);

        Assert.Equal(["Gym 1", "Gym 2", "Gym 3", "Champion"], page.Milestones.Select(m => m.Label));
        page.UsePresetCommand.Execute(90);
        Assert.Equal([27, 41, 56, 90], page.Milestones.Select(m => (int)m.Target)); // same spacing, Champion at 90
        page.ApplyCommand.Execute(null);

        Assert.Equal([27, 41, 56, 90], shell.Session.Data.Rules!.LevelCaps.Select(c => c.Level));
        Assert.Single(shell.Changes);
        var page2 = Assert.IsType<ProgressionPageViewModel>(shell.CurrentPage);
        page2.CapsEnabled = true;
        page2.ExpCapIndex = 1;
        page2.RareCandyCap = true;

        await SaveAsync(shell);
        Assert.Contains("{FLAG_IS_CHAMPION, 90}", File.ReadAllText(Path.Combine(root, "src", "caps.c")), StringComparison.Ordinal);
        var cfg = File.ReadAllText(Path.Combine(root, "include", "config", "caps.h"));
        Assert.Contains("#define B_LEVEL_CAP_TYPE                LEVEL_CAP_FLAG_LIST", cfg, StringComparison.Ordinal);
        Assert.Contains("#define B_EXP_CAP_TYPE                  EXP_CAP_HARD", cfg, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public async Task Physical_special_split_follows_modernize_and_shows_in_the_move_editor()
    {
        using var env = new TestEnvironment();
        var (shell, _) = await OpenFixtureAsync(env);
        var modernize = Open<ModernizePageViewModel>(shell, NavKey.Modernize);

        modernize.Generation = modernize.Generations.Single(g => g.Generation == 3);
        Assert.True(modernize.OffersSplit); // Gen 3 chosen while the game uses the split
        Assert.Contains("Gen 1–3 rule", modernize.SplitText, StringComparison.Ordinal);
        Assert.False(modernize.ChangeSplit); // going back keeps the modern split unless the user ticks it
        modernize.ChangeSplit = true;
        await modernize.PreviewCommand.ExecuteAsync(null);
        modernize.ApplyCommand.Execute(null);
        Assert.False(shell.Session.Data.Rules!.PhysicalSpecialSplit);
        Assert.Contains("Gen 1–3 move categories", Assert.Single(shell.Changes).Description, StringComparison.Ordinal);

        shell.SelectedMoveKey = "MOVE_EMBER_SNAP";
        var moves = Open<MovesPageViewModel>(shell, NavKey.Moves);
        Assert.Contains("Special because it's Fire type", moves.Editor!.SplitNote, StringComparison.Ordinal);

        shell.UndoCommand.Execute(null);
        Assert.True(shell.Session.Data.Rules!.PhysicalSpecialSplit);
    }

    [AvaloniaTheory]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.Light)]
    public async Task Capture_world_screens(ThemePreference theme)
    {
        var outDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "screens");
        Directory.CreateDirectory(outDir);
        using var env = new TestEnvironment();
        env.Main.Theme.Apply(theme);
        var window = new MainWindow { DataContext = env.Main, Width = 1440, Height = 900 };
        window.Show();
        void Save(string name)
        {
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var frame = window.CaptureRenderedFrame();
            Assert.NotNull(frame);
            frame.Save(Path.Combine(outDir, $"{name}-{theme}.png"));
        }

        var (shell, _) = await OpenFixtureAsync(env);
        shell.SelectedEncounterKey = "gFrostLake";
        Open<EncountersPageViewModel>(shell, NavKey.Encounters);
        Save("encounters");

        var items = Open<ItemsPageViewModel>(shell, NavKey.Items);
        items.ShowShops = true;
        Save("shops");

        shell.SelectedTextKey = "gText_LongSign";
        Open<TextPageViewModel>(shell, NavKey.Text);
        Save("text");

        Open<ProgressionPageViewModel>(shell, NavKey.Progression);
        Save("levels-caps");
        window.Close();
    }
}
