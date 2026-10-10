using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;
using PokeStudio.Core.Settings;

namespace PokeStudio.App.Tests;

/// <summary>Runs with the user's own Emerald ROM when POKESTUDIO_EMERALD_ROM is set (the app works on its own copy).</summary>
public class RomUiTests
{
    [AvaloniaFact]
    public async Task A_rom_becomes_a_project_with_its_pokemon_trainers_and_wild_areas()
    {
        var rom = Environment.GetEnvironmentVariable("POKESTUDIO_EMERALD_ROM");
        if (string.IsNullOrEmpty(rom) || !File.Exists(rom))
        {
            return;
        }

        using var env = new TestEnvironment();
        var outDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "screens");
        Directory.CreateDirectory(outDir);
        var window = new MainWindow { DataContext = env.Main, Width = 1440, Height = 900 };
        window.Show();
        var copy = Path.Combine(env.Root, "emerald.gba");
        File.Copy(rom, copy);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(copy));
        var shell = Assert.IsType<ShellViewModel>(env.Main.CurrentView);
        shell.IsChangesOpen = false;
        Assert.Equal(PokeStudio.Modules.Gen3Rom.Gen3RomModule.ModuleId, shell.Session.Module.Id);
        Assert.Contains(shell.NavItems, n => n.Key == NavKey.Trainers && n.IsEnabled);
        Assert.Contains(shell.NavItems, n => n.Key == NavKey.Encounters && n.IsEnabled);
        Assert.Contains(shell.NavItems, n => n.Key == NavKey.EvolutionFixes);
        Assert.DoesNotContain(shell.NavItems, n => n.Key == NavKey.GameOptions); // needs a decomp project

        shell.GoToSpecies("SPECIES_BULBASAUR");
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(outDir, "rom-species-Dark.png"));
        shell.GoToTrainer(shell.Session.Data.Trainers.First(t => t.Key.StartsWith("TRAINER_ROXANNE", StringComparison.Ordinal)).Key);
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(outDir, "rom-trainer-Dark.png"));

        // the original file the user picked is never changed by a save
        var before = await File.ReadAllBytesAsync(copy);
        var editor = Assert.IsType<TrainersPageViewModel>(shell.CurrentPage).Editor!;
        editor.NameDraft = "ROXY";
        await shell.SaveCommand.ExecuteAsync(null);
        if (shell.IsValidationOpen)
        {
            Assert.False(shell.HasErrors, string.Join(" | ", shell.Issues.Where(i => i.IsError).Select(i => i.Text)));
            await shell.SaveAnywayCommand.ExecuteAsync(null);
        }

        Assert.Equal("Saved.", shell.Toast);
        Assert.Equal(before, await File.ReadAllBytesAsync(copy));
        var working = await File.ReadAllBytesAsync(shell.Session.Project.WorkingSourcePath);
        Assert.NotEqual(before, working);
        window.Close();
    }
}
