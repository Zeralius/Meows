using Meows.Plugins.Abstractions;

namespace Meows.Services;

/// <summary>
/// The shell's end of a handoff. The main window knows the plugins and the tabs; this only
/// carries the two questions to it, so a <see cref="PluginHost"/> can be built before the
/// window is and without a reference to it.
/// </summary>
public sealed class HandoffService : IMeowsHandoff
{
    private readonly string _pluginId;
    private readonly Func<string, bool> _canReach;
    private readonly Func<string, string, Handoff, bool> _send;

    public HandoffService(string pluginId, Func<string, bool> canReach, Func<string, string, Handoff, bool> send)
    {
        _pluginId = pluginId;
        _canReach = canReach;
        _send = send;
    }

    public bool CanReach(string pluginId) => _canReach(pluginId);

    public bool Send(string pluginId, Handoff handoff) => _send(_pluginId, pluginId, handoff);
}
