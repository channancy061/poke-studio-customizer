using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using PokeStudio.Core.Branding;
using PokeStudio.Core.Changes;

namespace PokeStudio.App.ViewModels;

/// <summary>
/// The hack's own name and look: the ROM title, the built file's name, the name in the game's text, the title-screen
/// logo and the expansion's "Powered by" intro. Empty boxes keep (or put back) the game's own.
/// </summary>
public sealed partial class BrandingPageViewModel : ViewModelBase
{
    private readonly ShellViewModel _shell;
    private readonly BrandingSupport _support;

    public BrandingPageViewModel(ShellViewModel shell)
    {
        _shell = shell;
        _support = shell.Session.Module.DescribeBranding(shell.Session.Data)
            ?? new BrandingSupport("", "", "", null, new ImageSlot(0, 0, 0, ""), null);
        var b = shell.Session.Data.Branding ?? new BrandingSettings();
        RomTitle = b.RomTitle ?? "";
        FileName = b.FileName ?? "";
        VersionName = b.VersionName ?? "";
        ShowIntro = b.ShowExpansionIntro ?? _support.IntroShown ?? true;
        ShowLogo = b.HideLogo != true;
        ShowBoxArt = b.HideBoxArt != true;
        _logo = b.LogoPng;
        _subtitle = b.SubtitlePng;
        _background = b.BackgroundPng;
        BackgroundPreview = _support.Background is { } bg ? Preview(_background, bg) : null;
        LogoPreview = Preview(_logo, _support.Logo);
        SubtitlePreview = _support.Subtitle is { } s ? Preview(_subtitle, s) : null;
    }

    private string? _logo;
    private string? _subtitle;
    private string? _background;

    public string TitleHint => $"Now: {_support.CurrentTitle}. Up to {Brand.TitleLength} capital letters, digits and spaces. Emulators show it.";
    public string FileNameHint => $"Now: {_support.CurrentFileName}.gba. Letters, digits, '-' and '_'.";
    public string VersionNameHint => $"Now: {_support.CurrentVersionName}. Shown in the credits{(_support.Subtitle is null ? "" : " and wherever the game names its version")}.";
    public bool HasIntro => _support.IntroShown is not null;
    public string LogoHint => _support.Logo.Hint;
    public bool HasSubtitle => _support.Subtitle is not null;
    public string SubtitleHint => _support.Subtitle?.Hint ?? "";
    public bool HasBackgroundSlot => _support.Background is not null;
    public string BackgroundHint => _support.Background?.Hint ?? "";
    public bool HasBackgroundImage => _background is not null;

    [ObservableProperty]
    public partial Bitmap? BackgroundPreview { get; set; }

    [RelayCommand]
    private async Task PickBackgroundAsync()
    {
        if (_support.Background is { } slot)
        {
            _background = await PickAsync(slot, "background", _background, bmp => BackgroundPreview = bmp);
            OnPropertyChanged(nameof(HasBackgroundImage));
        }
    }

    [RelayCommand]
    private void ClearBackground()
    {
        _background = null;
        BackgroundPreview = null;
        OnPropertyChanged(nameof(HasBackgroundImage));
    }

    [ObservableProperty]
    public partial string RomTitle { get; set; }

    [ObservableProperty]
    public partial string FileName { get; set; }

    [ObservableProperty]
    public partial string VersionName { get; set; }

    [ObservableProperty]
    public partial bool ShowIntro { get; set; }

    [ObservableProperty]
    public partial bool ShowLogo { get; set; }

    [ObservableProperty]
    public partial bool ShowBoxArt { get; set; }

    public bool CanHideLayers => _support.CanHideLayers;

    [ObservableProperty]
    public partial Bitmap? LogoPreview { get; set; }

    [ObservableProperty]
    public partial Bitmap? SubtitlePreview { get; set; }

    [ObservableProperty]
    public partial string? Message { get; set; }

    public bool HasLogo => _logo is not null;
    public bool HasSubtitleImage => _subtitle is not null;

    private static Bitmap? Preview(string? png, ImageSlot slot) =>
        png is null ? null : Brand.Convert(Convert.FromBase64String(png), slot, "picture").Image is { } image ? SpriteEditorViewModel.ToBitmap(image) : null;

    [RelayCommand]
    private async Task PickLogoAsync() => _logo = await PickAsync(_support.Logo, "logo", _logo, bmp => LogoPreview = bmp);

    [RelayCommand]
    private async Task PickSubtitleAsync()
    {
        if (_support.Subtitle is { } slot)
        {
            _subtitle = await PickAsync(slot, "version line", _subtitle, bmp => SubtitlePreview = bmp);
        }
    }

    private async Task<string?> PickAsync(ImageSlot slot, string what, string? current, Action<Bitmap?> show)
    {
        var file = await _shell.Main.Dialogs.PickFileAsync($"Pick your {what} (PNG)", [("PNG pictures", ["*.png"])]);
        if (file is null)
        {
            return current;
        }

        var bytes = await File.ReadAllBytesAsync(file);
        var (image, problem) = Brand.Convert(bytes, slot, what);
        if (image is null)
        {
            Message = problem;
            return current;
        }

        Message = $"The {what} fits. Press Apply to use it.";
        show(SpriteEditorViewModel.ToBitmap(image));
        OnPropertyChanged(nameof(HasLogo));
        OnPropertyChanged(nameof(HasSubtitleImage));
        return Convert.ToBase64String(bytes);
    }

    [RelayCommand]
    private void ClearLogo()
    {
        _logo = null;
        LogoPreview = null;
        OnPropertyChanged(nameof(HasLogo));
    }

    [RelayCommand]
    private void ClearSubtitle()
    {
        _subtitle = null;
        SubtitlePreview = null;
        OnPropertyChanged(nameof(HasSubtitleImage));
    }

    [RelayCommand]
    private void Apply()
    {
        static string? Value(string text) => text.Trim() is { Length: > 0 } t ? t : null;
        var title = Value(RomTitle)?.ToUpperInvariant();
        var file = Value(FileName);
        var name = Value(VersionName);
        var problem = (title is null ? null : Brand.CheckTitle(title)) ?? (file is null ? null : Brand.CheckFileName(file)) ?? (name is null ? null : Brand.CheckVersionName(name));
        if (problem is not null)
        {
            Message = problem;
            return;
        }

        var data = _shell.Session.Data;
        var settings = new BrandingSettings
        {
            RomTitle = title,
            FileName = file,
            VersionName = name,
            ShowExpansionIntro = HasIntro && (ShowIntro != _support.IntroShown || data.Branding?.ShowExpansionIntro is not null) ? ShowIntro : null,
            LogoPng = _logo,
            SubtitlePng = _subtitle,
            BackgroundPng = _background,
            HideLogo = CanHideLayers && !ShowLogo ? true : null,
            HideBoxArt = CanHideLayers && !ShowBoxArt ? true : null,
        };
        if (settings == (data.Branding ?? new BrandingSettings()))
        {
            Message = "Nothing changed.";
            return;
        }

        _shell.Execute(new ValueChange<BrandingSettings?>("Name & title screen", "Game name and title screen", v => data.Branding = v, data.Branding, settings));
        RomTitle = title ?? "";
        Message = null;
        _shell.Toast = "Saved with the project. The game gets the new name and title screen when you save and build. Ctrl+Z undoes it.";
    }
}
