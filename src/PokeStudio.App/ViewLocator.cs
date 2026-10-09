using Avalonia.Controls;
using Avalonia.Controls.Templates;
using PokeStudio.App.ViewModels;

namespace PokeStudio.App;

/// <summary>Finds the view for a view model by name: ViewModels.FooViewModel → Views.FooView.</summary>
public sealed class ViewLocator : IDataTemplate
{
    public Control? Build(object? param)
    {
        if (param is null)
        {
            return null;
        }

        var name = param.GetType().FullName!.Replace(".ViewModels.", ".Views.", StringComparison.Ordinal)
            .Replace("ViewModel", "View", StringComparison.Ordinal);
        var type = Type.GetType(name);
        return type is null
            ? new TextBlock { Text = "Missing view: " + name }
            : (Control)Activator.CreateInstance(type)!;
    }

    public bool Match(object? data) => data is ViewModelBase;
}
