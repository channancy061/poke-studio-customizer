using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.Core.Projects;

namespace PokeStudio.App.Tests;

public class BrandingUiTests
{
    [AvaloniaFact]
    public async Task The_hack_gets_its_own_title_file_name_and_no_powered_by_screen()
    {
        using var env = new TestEnvironment();
        var root = Path.Combine(env.Root, "my-hack");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), root);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(root));
        var shell = Assert.IsType<ShellViewModel>(env.Main.CurrentView);
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.Branding);
        var page = Assert.IsType<BrandingPageViewModel>(shell.CurrentPage);
        Assert.Contains("POKEMON EMER", page.TitleHint, StringComparison.Ordinal);
        Assert.True(page.HasIntro);
        Assert.True(page.ShowIntro);

        // a title too long is refused; a good one is one undo step
        page.RomTitle = "CROSSOVER RED";
        page.ApplyCommand.Execute(null);
        Assert.NotNull(page.Message);
        Assert.Null(shell.Session.Data.Branding);
        page.RomTitle = "crossover rd";
        page.FileName = "crossover";
        page.ShowIntro = false;
        page.ApplyCommand.Execute(null);
        Assert.Equal("CROSSOVER RD", shell.Session.Data.Branding!.RomTitle);
        Assert.False(shell.Session.Data.Branding.ShowExpansionIntro);

        await shell.SaveCommand.ExecuteAsync(null);
        if (shell.IsValidationOpen)
        {
            await shell.SaveAnywayCommand.ExecuteAsync(null);
        }

        var make = File.ReadAllText(Path.Combine(root, "Makefile"));
        Assert.Contains("TITLE        ?= CROSSOVER RD\n", make, StringComparison.Ordinal);
        Assert.Contains("FILE_NAME := crossover\n", make, StringComparison.Ordinal);
        Assert.Contains("#define EXPANSION_INTRO              FALSE", File.ReadAllText(Path.Combine(root, "include", "config", "general.h")), StringComparison.Ordinal);
        Assert.Equal("crossover.gba", shell.Session.Module.Build is null ? null : ((PokeStudio.Modules.Expansion.ExpansionModule)shell.Session.Module).BuildFor(root).OutputFile);
    }
}
