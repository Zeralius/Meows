using Avalonia;
using Avalonia.Controls;
using Meows.Plugins.Abstractions;

namespace Meow;

/// <summary>
/// The least a plugin can be: one public class with a parameterless constructor, the four
/// members that have no default, and a control. No view model, no XAML, no string catalogue,
/// no theme colours. Everything else on the contract is optional, and this is the proof.
/// </summary>
public sealed class MeowPlugin : IMeowsPlugin
{
    /// <summary>Stable forever: it is the settings key and the activation record.</summary>
    public string Id => "example.meow";

    public string DisplayName => "Meow";

    /// <summary>Not a key, so it is shown exactly as written, in every language.</summary>
    public string Description => "The least a plugin can be: one class, one control, nothing else.";

    public string? Icon => "🐾";

    /// <summary>Any text makes a group of its own on the Plugins tab. The five examples share this one.</summary>
    public string? Category => "Examples";

    public Control CreateView(IMeowsHost host) => new TextBlock
    {
        Text = $"Meow. Anything this plugin saved would live in {host.DataDirectory}.",
        TextWrapping = Avalonia.Media.TextWrapping.Wrap,
        Margin = new Thickness(20),
    };
}
