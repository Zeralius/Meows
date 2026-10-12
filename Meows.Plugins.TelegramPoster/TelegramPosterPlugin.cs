using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Perch;
using Meows.Plugins.TelegramPoster.ViewModels;
using Meows.Plugins.TelegramPoster.Views;

namespace Meows.Plugins.TelegramPoster;

/// <summary>
/// The posting bot, in two pages: its groups and queues, settings and the bot itself, and the one
/// timeline of what goes out tonight and after (Perch until 6.1.0). Telegram Poster keeps its id,
/// so its settings and history carry straight on; Perch reads its own from a folder under it.
/// </summary>
public sealed class TelegramPosterPlugin() : PagedPlugin(new QueuesPage(), new PerchPlugin())
{
    public override string PlainName => "tp.name.plain";

    public override string Description => "tp.description";
}

/// <summary>The first page: the bot's groups and queues, as Telegram Poster has always shown them.</summary>
internal sealed class QueuesPage : IMeowsPlugin
{
    public string Id => "meows.telegram-poster";

    public string DisplayName => "Telegram Poster";

    public string PlainName => "tp.page.plain";

    public string Description => "tp.page.description";

    public string Icon => "✈";

    public string Category => "group.bot";

    public PluginTopics Topics => PluginTopics.Social;

    public IReadOnlyList<RecordedKind> Records =>
    [
        new("held", "tp.records.held"),
    ];

    public Control CreateView(IMeowsHost host) => new TelegramPosterView
    {
        DataContext = new TelegramPosterViewModel(host),
    };
}
