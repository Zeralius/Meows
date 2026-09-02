using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.TelegramPoster.ViewModels;
using Meows.Plugins.TelegramPoster.Views;

namespace Meows.Plugins.TelegramPoster;

public sealed class TelegramPosterPlugin : IMeowsPlugin
{
    public string Id => "meows.telegram-poster";

    public string DisplayName => "Telegram Poster";

    public string Description =>
        "Browse the posting bot's groups and queues, preview what goes out next, edit group settings, and run the bot.";

    public string Icon => "✈";

    public string Category => "Posting bot";

    public Control CreateView(IMeowsHost host) => new TelegramPosterView
    {
        DataContext = new TelegramPosterViewModel(host),
    };
}
