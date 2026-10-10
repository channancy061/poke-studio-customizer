using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using PokeStudio.App.ViewModels;
using PokeStudio.App.Views;
using PokeStudio.Core.Graphics;
using PokeStudio.Core.Projects;
using PokeStudio.Core.Settings;

namespace PokeStudio.App.Tests;

public class GraphicsUiTests
{
    private static readonly Rgb[] Colors = [.. Enumerable.Range(0, 16).Select(i => new Rgb((byte)(i * 16), (byte)(200 - (i * 8)), (byte)(i * 12)))];

    private static IndexedImage Sample(int width, int height) =>
        new(width, height, [.. Enumerable.Range(0, width * height).Select(i => (byte)(((i % width) / 4 + (i / width) / 4) % 16))], Colors);

    /// <summary>Gives the mini project's first Pokémon real sprite files, wired up the way pokeemerald-expansion does it.</summary>
    private static void AddSprites(string root)
    {
        var dir = Path.Combine(root, "graphics", "pokemon", "sproutle");
        Directory.CreateDirectory(dir);
        Directory.CreateDirectory(Path.Combine(root, "graphics", "pokemon", "icon_palettes"));
        File.WriteAllBytes(Path.Combine(dir, "anim_front.png"), Png.Write(Sample(64, 128), 4));
        File.WriteAllBytes(Path.Combine(dir, "back.png"), Png.Write(Sample(64, 64), 4));
        File.WriteAllBytes(Path.Combine(dir, "icon.png"), Png.Write(Sample(32, 64), 4));
        File.WriteAllText(Path.Combine(dir, "normal.pal"), JascPalette.Write(Colors));
        File.WriteAllText(Path.Combine(dir, "shiny.pal"), JascPalette.Write([.. Colors.Reverse()]));
        File.WriteAllText(Path.Combine(root, "graphics", "pokemon", "icon_palettes", "pal0.pal"), JascPalette.Write(Colors));
        File.WriteAllText(Path.Combine(root, "src", "data", "graphics", "pokemon.h").Also(p => Directory.CreateDirectory(Path.GetDirectoryName(p)!)), """
            const u32 gMonFrontPic_Sproutle[] = INCGFX_U32("graphics/pokemon/sproutle/anim_front.png", ".4bpp.smol");
            const u16 gMonPalette_Sproutle[] = INCGFX_U16("graphics/pokemon/sproutle/normal.pal", ".gbapal");
            const u32 gMonBackPic_Sproutle[] = INCGFX_U32("graphics/pokemon/sproutle/back.png", ".4bpp.smol");
            const u16 gMonShinyPalette_Sproutle[] = INCGFX_U16("graphics/pokemon/sproutle/shiny.pal", ".gbapal");
            const u8 gMonIcon_Sproutle[] = INCGFX_U8("graphics/pokemon/sproutle/icon.png", ".4bpp");

            """);
        var info = Path.Combine(root, "src", "data", "pokemon", "species_info", "gen_1_families.h");
        var text = File.ReadAllText(info);
        var at = text.IndexOf('{', text.IndexOf("[SPECIES_SPROUTLE]", StringComparison.Ordinal)) + 1;
        File.WriteAllText(info, text.Insert(at, """

                    .frontPic = gMonFrontPic_Sproutle,
                    .backPic = gMonBackPic_Sproutle,
                    .palette = gMonPalette_Sproutle,
                    .shinyPalette = gMonShinyPalette_Sproutle,
                    .iconSprite = gMonIcon_Sproutle,
                    .iconPalIndex = 0,
            """));
    }

    [AvaloniaTheory]
    [InlineData(ThemePreference.Dark)]
    [InlineData(ThemePreference.Light)]
    public async Task Sprites_can_be_recoloured_replaced_and_saved(ThemePreference theme)
    {
        var outDir = Path.Combine(AppContext.BaseDirectory, "TestResults", "screens");
        Directory.CreateDirectory(outDir);
        using var env = new TestEnvironment();
        env.Main.Theme.Apply(theme);
        var window = new MainWindow { DataContext = env.Main, Width = 1440, Height = 900 };
        window.Show();
        var root = Path.Combine(env.Root, "my-hack");
        ProjectService.CopyDirectory(Path.Combine(AppContext.BaseDirectory, "Fixtures", "expansion-mini"), root);
        AddSprites(root);
        Assert.Null(await env.Main.CreateProjectFromGameAsync(root));
        var shell = Assert.IsType<ShellViewModel>(env.Main.CurrentView);
        shell.IsChangesOpen = false;
        shell.SelectedSpeciesKey = "SPECIES_SPROUTLE";
        shell.SelectedNav = shell.NavItems.Single(n => n.Key == NavKey.Graphics && n.IsEnabled);
        var page = Assert.IsType<GraphicsPageViewModel>(shell.CurrentPage);
        var editor = Assert.IsType<SpriteEditorViewModel>(page.Editor);
        Assert.Equal(3, editor.Views.Count);
        Assert.NotNull(editor.Picture);
        Assert.Equal(16, editor.Swatches.Count);
        Assert.Equal(256, editor.PictureWidth);

        // shiny colours, then a colour change (one undo step)
        editor.Shiny = true;
        Assert.Equal(Colors[^1].Hex, editor.Swatches[0].Hex);
        editor.Shiny = false;
        editor.Swatches[3].Hex = "#F80000";
        Assert.True(shell.IsDirty);
        Assert.True(editor.IsEdited);
        Assert.Equal("#F80000", editor.Swatches[3].Hex);

        // a picture of the wrong size is refused with a reason; a right-sized one replaces the front sprite
        var wrong = Path.Combine(env.Root, "wrong.png");
        File.WriteAllBytes(wrong, Png.Write(Sample(40, 40), 8));
        Assert.Contains("64×64", editor.ReplaceFrom(wrong), StringComparison.Ordinal);
        var picture = Path.Combine(env.Root, "new.png");
        var drawn = Sample(64, 64) with { Pixels = [.. Enumerable.Range(0, 64 * 64).Select(i => (byte)(i % 3))] };
        File.WriteAllBytes(picture, Png.Write(drawn, 8));
        Assert.Null(editor.ReplaceFrom(picture));

        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        window.CaptureRenderedFrame()!.Save(Path.Combine(outDir, $"graphics-{theme}.png"));

        var exported = editor.Export();
        Assert.True(File.Exists(exported));

        await shell.SaveCommand.ExecuteAsync(null);
        if (shell.IsValidationOpen)
        {
            Assert.False(shell.HasErrors, string.Join(" | ", shell.Issues.Where(i => i.IsError).Select(i => i.Text)));
            await shell.SaveAnywayCommand.ExecuteAsync(null);
        }

        Assert.Equal("Saved.", shell.Toast);
        var front = Assert.IsType<IndexedImage>(Png.Read(File.ReadAllBytes(Path.Combine(root, "graphics", "pokemon", "sproutle", "anim_front.png"))));
        Assert.Equal((64, 128), (front.Width, front.Height)); // one frame repeated for the animation
        Assert.Equal(3, front.Pixels.Distinct().Count());
        Assert.Equal(Sample(64, 64).Pixels, ((IndexedImage)Png.Read(File.ReadAllBytes(Path.Combine(root, "graphics", "pokemon", "sproutle", "back.png")))).Pixels);

        // keep what was written for review (the temporary project is deleted afterwards)
        var keep = Path.Combine(outDir, $"graphics-output-{theme}");
        if (Directory.Exists(keep))
        {
            Directory.Delete(keep, true);
        }

        ProjectService.CopyDirectory(Path.Combine(root, "graphics", "pokemon", "sproutle"), Path.Combine(keep, "saved-sprite-files"));
        File.Copy(exported!, Path.Combine(keep, Path.GetFileName(exported!)));
        window.Close();
    }
}

internal static class TestExtensions
{
    public static T Also<T>(this T value, Action<T> action)
    {
        action(value);
        return value;
    }
}
