using Avalonia.Controls;
using Mews.Plugins.Abstractions;
using Mews.Plugins.TelegramPoster.ViewModels;
using Mews.Plugins.TelegramPoster.Views;

namespace Mews.Plugins.TelegramPoster;

public sealed class TelegramPosterPlugin : IMewsPlugin
{
    public string Id => "mews.telegram-poster";

    public string DisplayName => "Telegram Poster";

    public string Description =>
        "Browse the posting bot's groups and queues, preview what goes out next, edit group settings, and run the bot.";

    public string Icon => "✈";

    public Control CreateView(IMewsHost host) => new TelegramPosterView
    {
        DataContext = new TelegramPosterViewModel(host),
    };
}
