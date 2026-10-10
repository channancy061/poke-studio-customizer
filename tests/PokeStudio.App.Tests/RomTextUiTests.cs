using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;

namespace PokeStudio.App.Tests;

/// <summary>Dialogue editing in ROM mode, with the user's own ROMs when their environment variables are set (on copies).</summary>
public class RomTextUiTests
{
    private static async Task<(TestEnvironment Env, MainWindow Window, ShellViewModel Shell)?> Open(string variable, string name)
    {
        var rom = Environment.GetEnvironmentVariable(variable);
        if (string.IsNullOrEmpty(rom) || !File.Exists(rom))
        {
            return null;
        }

        var env = new TestEnvironment();
        var window = new MainWindow { DataContext = env.Main, Width = 1440, Height = 900 };
        window.Show();
        var copy = Path.Combine(env.Root, name);
        File.Copy(rom, copy);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(copy));
        var shell = Assert.IsType<ShellViewModel>(env.Main.CurrentView);
        shell.IsChangesOpen = false;
        return (env, window, shell);
    }

    [AvaloniaFact]
    public async Task Ds_dialogue_keeps_its_own_break_codes_when_edited()
    {
        if (await Open("POKESTUDIO_PLATINUM_ROM", "platinum.nds") is not var (env, window, shell))
        {
            return;
        }

        using (env)
        {
            var text = shell.Session.Data.Texts.First(t => t.Text.Contains("\\r", StringComparison.Ordinal) && t.Text.Contains("\\n", StringComparison.Ordinal) && !t.Text.Contains('{', StringComparison.Ordinal));
            shell.SelectedTextKey = text.Key;
            shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.Text && n.IsEnabled);
            var editor = Assert.IsType<TextPageViewModel>(shell.CurrentPage).Editor!;
            Assert.Contains("\\r", editor.Draft, StringComparison.Ordinal);
            Assert.Contains("\\f starts a new box", editor.BreaksHelp, StringComparison.Ordinal);

            editor.Draft += "\nA new line.";
            var edited = shell.Session.Data.Texts.Single(t => t.Key == text.Key).Text;
            Assert.Equal(text.Text + "\\nA new line.", edited);
            Assert.True(editor.Preview.Count > 2);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var outDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "screens");
            Directory.CreateDirectory(outDir);
            window.CaptureRenderedFrame()!.Save(Path.Combine(outDir, "rom-text-platinum-Dark.png"));

            await shell.SaveCommand.ExecuteAsync(null);
            if (shell.IsValidationOpen)
            {
                Assert.False(shell.HasErrors, string.Join(" | ", shell.Issues.Where(i => i.IsError).Select(i => i.Text)));
                await shell.SaveAnywayCommand.ExecuteAsync(null);
            }

            Assert.Equal("Saved.", shell.Toast);
            window.Close();
        }
    }

    [AvaloniaFact]
    public async Task Gba_rom_dialogue_is_listed_by_place()
    {
        if (await Open("POKESTUDIO_EMERALD_ROM", "emerald.gba") is not var (env, window, shell))
        {
            return;
        }

        using (env)
        {
            shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.Text && n.IsEnabled);
            var page = Assert.IsType<TextPageViewModel>(shell.CurrentPage);
            page.Search = "Littleroot";
            Assert.NotEmpty(page.Items);
            Assert.Contains("Each line is one line", page.Editor!.BreaksHelp, StringComparison.Ordinal);
            Avalonia.Threading.Dispatcher.UIThread.RunJobs();
            var outDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "screens");
            Directory.CreateDirectory(outDir);
            window.CaptureRenderedFrame()!.Save(Path.Combine(outDir, "rom-text-emerald-Dark.png"));
            window.Close();
        }
    }
}
