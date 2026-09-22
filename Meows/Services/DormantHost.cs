using Meows.Plugins.Abstractions;

namespace Meows.Services;

/// <summary>
/// What a plugin that is switched off is given to answer Ctrl+K from: its settings, its
/// journal, and a way to hand itself the hit. The same folder, store and handoff as the real
/// host would give, and nothing that would let it act while off.
/// </summary>
public sealed class DormantHost : IMeowsDormantHost
{
    private readonly ShellSettings _settings;

    public DormantHost(string pluginId, ShellSettings settings, IMeowsHandoff handoff, IMeowsStore? store)
    {
        PluginId = pluginId;
        _settings = settings;
        DataDirectory = settings.PluginDataDirectory(pluginId);
        Handoff = handoff;
        Store = store ?? NoStore.Instance;
    }

    public string PluginId { get; }

    public string DataDirectory { get; }

    public IMeowsText Text => MeowsText.Current;

    public IMeowsStore Store { get; }

    public IMeowsHandoff Handoff { get; }

    public T? LoadSettings<T>() where T : class => _settings.LoadPluginSettings<T>(PluginId);
}
