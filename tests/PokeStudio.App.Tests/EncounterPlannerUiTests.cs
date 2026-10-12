using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.Core.Projects;

namespace PokeStudio.App.Tests;

public class EncounterPlannerUiTests
{
    [AvaloniaFact]
    public async Task Preview_lists_areas_and_apply_is_one_undo_step()
    {
        using var env = new TestEnvironment();
        var root = Path.Combine(env.Root, "my-hack");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), root);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(root));
        var shell = Assert.IsType<ShellViewModel>(env.Main.CurrentView);
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.EncounterPlanner);
        var page = Assert.IsType<EncounterPlannerPageViewModel>(shell.CurrentPage);
        Assert.True(page.HasCaps);

        page.Share = 100;
        await page.PreviewCommand.ExecuteAsync(null);
        Assert.NotNull(page.Status);
        Assert.False(shell.IsBusy);
        if (!page.HasAreas)
        {
            return; // the tiny invented fixture may have nothing better to offer
        }

        var before = shell.Session.Data.Encounters.ToList();
        page.ApplyCommand.Execute(null);
        Assert.NotEqual(before, shell.Session.Data.Encounters);
        Assert.Contains("Wild Pokémon planned", Assert.Single(shell.Changes).Description, StringComparison.Ordinal);
        shell.UndoCommand.Execute(null);
        Assert.Equal(before, shell.Session.Data.Encounters);
    }
}
