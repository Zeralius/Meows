using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Perch.ViewModels;
using Meows.Plugins.Perch.Views;

namespace Meows.Plugins.Perch;

/// <summary>A page of Telegram Poster since 6.1.0, under its own id for its settings; not a plugin of its own.</summary>
internal sealed class PerchPlugin : IMeowsPlugin
{
    public string Id => "meows.perch";

    public string DisplayName => "Perch";

    public string PlainName => "perch.name.plain";

    public string Description => "perch.description";

    public string? Icon => "🪶";

    public string Category => "group.bot";

    public PluginTopics Topics => PluginTopics.Social;

    public Control CreateView(IMeowsHost host) => new PerchView
    {
        DataContext = new PerchViewModel(host),
    };
}
