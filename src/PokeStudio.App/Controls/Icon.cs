using Avalonia;
using Avalonia.Controls.Primitives;
using Avalonia.Media;

namespace PokeStudio.App.Controls;

/// <summary>
/// Outline icon drawn on a 24-unit grid with a 2-unit stroke in the current Foreground colour.
/// Geometries live in Theme/Icons.axaml (original drawings in the style of the Lucide set).
/// Decorative: give the parent control an accessible name.
/// </summary>
public class Icon : TemplatedControl
{
    public static readonly StyledProperty<Geometry?> DataProperty =
        AvaloniaProperty.Register<Icon, Geometry?>(nameof(Data));

    public static readonly StyledProperty<double> SizeProperty =
        AvaloniaProperty.Register<Icon, double>(nameof(Size), 16);

    public Geometry? Data
    {
        get => GetValue(DataProperty);
        set => SetValue(DataProperty, value);
    }

    public double Size
    {
        get => GetValue(SizeProperty);
        set => SetValue(SizeProperty, value);
    }
}
