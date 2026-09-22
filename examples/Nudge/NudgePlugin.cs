using Avalonia.Controls;
using Meows.Plugins.Abstractions;

namespace Nudge;

public sealed class NudgePlugin : IMeowsPlugin
{
    public string Id => "example.nudge";

    public string DisplayName => "Nudge";

    public string PlainName => "nudge.name.plain";

    public string Description => "nudge.description";

    public string? Icon => "👉";

    public string? Category => "Examples";

    public Control CreateView(IMeowsHost host) => new NudgeView
    {
        DataContext = new NudgeViewModel(host),
    };
}
