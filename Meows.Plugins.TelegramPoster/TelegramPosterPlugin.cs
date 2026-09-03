using Avalonia.Controls;
using Meows.Plugins.Abstractions;
using Meows.Plugins.TelegramPoster.ViewModels;
using Meows.Plugins.TelegramPoster.Views;

namespace Meows.Plugins.TelegramPoster;

public sealed class TelegramPosterPlugin : IMeowsPlugin
{
    public string Id => "meows.telegram-poster";

    public string DisplayName => "Telegram Poster";

    public string Description => "tp.description";

    public string Icon => "✈";

    public string Category => "group.bot";

    public Control CreateView(IMeowsHost host) => new TelegramPosterView
    {
        DataContext = new TelegramPosterViewModel(host),
    };
}
