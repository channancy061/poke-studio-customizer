using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeStudio.Core.Graphics;

namespace PokeStudio.App.ViewModels;

/// <summary>The graphics studio: each Pokémon's sprites, their colours, and replacing them from a picture.</summary>
public sealed class GraphicsPageViewModel(ShellViewModel shell) : SpeciesBrowserViewModel(shell, "Graphics")
{
    protected override ViewModelBase CreateEditor(int speciesIndex) => new SpriteEditorViewModel(Shell, speciesIndex);
}

public sealed record SpriteViewOption(SpriteKind Kind, string Label);

/// <summary>One palette colour, edited as #RRGGBB.</summary>
public sealed partial class SwatchViewModel(SpriteEditorViewModel owner, int index, Rgb color, bool editable) : ViewModelBase
{
    public int Index { get; } = index;
    public bool IsEditable { get; } = editable;
    public string Tip => Index == 0 ? "Colour 0 is see-through in the game (the background)." : $"Colour {Index}";
    public Avalonia.Media.IBrush Brush { get; } = new Avalonia.Media.SolidColorBrush(Avalonia.Media.Color.FromRgb(color.R, color.G, color.B));

    [ObservableProperty]
    public partial string Hex { get; set; } = color.Hex;

    partial void OnHexChanged(string value) => owner.SetColor(Index, value);
}

public sealed partial class SpriteEditorViewModel : ViewModelBase, IRefreshable
{
    private readonly ShellViewModel _shell;
    private readonly ISpriteSource _source;
    private bool _loading;

    public SpriteEditorViewModel(ShellViewModel shell, int index)
    {
        _shell = shell;
        _source = (ISpriteSource)shell.Session.Module;
        var species = shell.Session.Data.Species[index];
        SpeciesKey = species.Key;
        Name = species.DisplayName;
        Views =
        [
            .. new[] { (SpriteKind.Front, "Front"), (SpriteKind.Back, "Back"), (SpriteKind.Icon, "Party icon") }
                .Where(v => _source.Rules(Data, SpeciesKey, v.Item1) is not null)
                .Select(v => new SpriteViewOption(v.Item1, v.Item2)),
        ];
        SelectedView = Views.FirstOrDefault();
        Refresh();
    }

    private Core.Modules.GameData Data => _shell.Session.Data;

    public string SpeciesKey { get; }
    public string Name { get; }
    public IReadOnlyList<SpriteViewOption> Views { get; }
    public bool HasSprites => Views.Count > 0;
    public bool NoSprites => !HasSprites;
    public ObservableCollection<SwatchViewModel> Swatches { get; } = [];

    [ObservableProperty]
    public partial SpriteViewOption? SelectedView { get; set; }

    [ObservableProperty]
    public partial bool Shiny { get; set; }

    [ObservableProperty]
    public partial Bitmap? Picture { get; set; }

    [ObservableProperty]
    public partial double PictureWidth { get; set; }

    [ObservableProperty]
    public partial double PictureHeight { get; set; }

    [ObservableProperty]
    public partial string? Status { get; set; }

    public SpriteRules? Rules => SelectedView is { } v ? _source.Rules(Data, SpeciesKey, v.Kind) : null;
    public bool IsIcon => SelectedView?.Kind == SpriteKind.Icon;
    public bool CanShiny => !IsIcon;

    public string RulesText => Rules is not { } r
        ? ""
        : $"{r.Width}×{r.FrameHeight} pixels{(r.Frames > 1 ? $", {r.Frames} frames" : "")}, up to {r.Colors} colours (colour 0 is the see-through background)."
            + (r.Note is null ? "" : " " + r.Note);

    public string PaletteText => IsIcon
        ? "Party icons use a palette shared with other Pokémon, so their colours can't be changed here."
        : Shiny ? "Shiny colours: the same picture, painted with these." : "Normal colours. Type a new #RRGGBB to change one; the game rounds colours slightly.";

    public bool IsEdited => Data.Graphics.Images.Keys.Concat(Data.Graphics.Palettes.Keys).Any(k => k.StartsWith(SpeciesKey + "|", StringComparison.Ordinal));

    partial void OnSelectedViewChanged(SpriteViewOption? value)
    {
        if (value?.Kind == SpriteKind.Icon)
        {
            Shiny = false;
        }

        Refresh();
    }

    partial void OnShinyChanged(bool value) => Refresh();

    public void Refresh()
    {
        _loading = true;
        try
        {
            Swatches.Clear();
            Picture = null;
            if (SelectedView is not { } view)
            {
                return;
            }

            var image = SpriteEditing.Current(_source, Data, SpeciesKey, view.Kind, Shiny);
            if (image is not null)
            {
                Picture = ToBitmap(image);
                var scale = view.Kind == SpriteKind.Icon ? 6 : 4;
                PictureWidth = image.Width * scale;
                PictureHeight = image.Height * scale;
                var palette = view.Kind == SpriteKind.Icon ? image.Palette : SpriteEditing.CurrentPalette(_source, Data, SpeciesKey, Shiny) ?? image.Palette;
                for (var i = 0; i < Math.Min(16, palette.Count); i++)
                {
                    Swatches.Add(new SwatchViewModel(this, i, palette[i], !IsIcon));
                }
            }
        }
        finally
        {
            _loading = false;
        }

        OnPropertyChanged(nameof(Rules));
        OnPropertyChanged(nameof(RulesText));
        OnPropertyChanged(nameof(PaletteText));
        OnPropertyChanged(nameof(IsIcon));
        OnPropertyChanged(nameof(CanShiny));
        OnPropertyChanged(nameof(IsEdited));
    }

    internal static WriteableBitmap ToBitmap(IndexedImage image)
    {
        var bitmap = new WriteableBitmap(new PixelSize(image.Width, image.Height), new Vector(96, 96), PixelFormat.Rgba8888, AlphaFormat.Unpremul);
        var rgba = image.ToRgba();
        using var frame = bitmap.Lock();
        for (var y = 0; y < image.Height; y++)
        {
            System.Runtime.InteropServices.Marshal.Copy(rgba, y * image.Width * 4, frame.Address + (y * frame.RowBytes), image.Width * 4);
        }

        return bitmap;
    }

    internal void SetColor(int index, string hex)
    {
        if (_loading || IsIcon || Rgb.Parse(hex) is not { } color)
        {
            return;
        }

        var palette = SpriteEditing.CurrentPalette(_source, Data, SpeciesKey, Shiny)?.ToList();
        if (palette is null || index >= palette.Count || palette[index].ToGba() == color.ToGba())
        {
            return;
        }

        palette[index] = color.ToGba();
        _shell.Execute(SpriteEditing.SetPalette(Data, SpeciesKey, Shiny, palette));
    }

    /// <summary>Fits a picture file to this sprite and stages it (one undo step).</summary>
    public string? ReplaceFrom(string file)
    {
        if (SelectedView is not { } view || Rules is not { } rules)
        {
            return "This sprite can't be replaced.";
        }

        object png;
        try
        {
            png = Png.Read(File.ReadAllBytes(file));
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException)
        {
            return "That file couldn't be read as a PNG picture: " + ex.Message;
        }

        var fixedPalette = rules.OwnPalette ? null : SpriteEditing.Current(_source, Data, SpeciesKey, view.Kind, false)?.Palette;
        var fitted = SpriteEditing.Prepare(SpriteEditing.ToRgba(png), rules, fixedPalette, out var problem);
        if (fitted is null)
        {
            return problem;
        }

        _shell.Execute(SpriteEditing.Replace(Data, SpeciesKey, view.Kind, fitted));
        Refresh();
        return null;
    }

    public string ExportFolder => Path.Combine(_shell.Session.Project.Directory, "graphics-export");

    /// <summary>Writes the sprite as shown (a paletted PNG others can edit and import again).</summary>
    public string? Export()
    {
        if (SelectedView is not { } view || SpriteEditing.Current(_source, Data, SpeciesKey, view.Kind, Shiny) is not { } image)
        {
            return null;
        }

        Directory.CreateDirectory(ExportFolder);
        var name = $"{SpeciesKey.Replace("SPECIES_", "", StringComparison.Ordinal).ToLowerInvariant()}-{view.Kind.ToString().ToLowerInvariant()}{(Shiny ? "-shiny" : "")}.png";
        var path = Path.Combine(ExportFolder, name);
        File.WriteAllBytes(path, Png.Write(image, 4));
        return path;
    }

    [RelayCommand]
    private async Task ReplaceAsync()
    {
        var file = await _shell.Main.Dialogs.PickFileAsync("Pick a picture for " + Name, [("PNG pictures", ["*.png"])]);
        if (file is not null)
        {
            Status = ReplaceFrom(file) ?? "Replaced. The colours came from your picture; adjust the shiny colours if you like. Save to write it into the game.";
        }
    }

    [RelayCommand]
    private void ExportPng()
    {
        try
        {
            if (Export() is { } path)
            {
                Status = "Exported to " + path;
                _shell.Main.ShowInFolder(ExportFolder);
            }
        }
        catch (IOException ex)
        {
            Status = "Couldn't export: " + ex.Message;
        }
    }
}
