using System.Text.Encodings.Web;
using System.Text.Json;
using Meows.Plugins;
using Meows.Plugins.Abstractions;
using Meows.ViewModels;

namespace Meows.Services;

/// <summary>One switched-on plugin's line, as <c>--glance</c> prints it.</summary>
/// <param name="FromPlugin">The plugin said it; false when it is the shell's own line, the last thing it recorded.</param>
public sealed record GlanceLine(string Id, string Name, string Text, bool IsTrouble, bool FromPlugin);

/// <summary>
/// The Home tab without the window: every switched-on plugin's one sentence, for a terminal, a
/// status bar or a scheduled task that wants to know whether anything needs doing.
///
/// A plugin is asked through <see cref="IMeowsPlugin.GlanceWhileOff"/> with the same dormant
/// host Ctrl+K gives a plugin that is off, since there is no view to ask. One that has nothing
/// to say gets the line its card on the Plugins tab carries: the last thing it recorded, and
/// how long ago.
/// </summary>
public static class GlanceReport
{
    public static IReadOnlyList<GlanceLine> Gather(
        IEnumerable<PluginDescriptor> found,
        IReadOnlySet<string> switchedOn,
        ShellSettings settings,
        MeowsStore? store,
        Action<string> log)
    {
        var text = MeowsText.Current;
        var lines = new List<GlanceLine>();

        foreach (var descriptor in found
                     .Where(d => d.IsCompatible && switchedOn.Contains(d.Id))
                     .OrderBy(d => d.Name, StringComparer.CurrentCultureIgnoreCase))
        {
            Glance? said = null;
            try
            {
                var host = new DormantHost(descriptor.Id, settings, NoHandoff.Instance, store?.For(descriptor.Id));
                said = descriptor.Plugin!.GlanceWhileOff(host);
            }
            catch (Exception ex)
            {
                log($"'{descriptor.DisplayName}' failed to glance with no window: {ex.Message}");
            }

            if (said is { Text.Length: > 0 })
            {
                lines.Add(new GlanceLine(descriptor.Id, descriptor.Name, said.Text, said.IsTrouble, FromPlugin: true));
                continue;
            }

            var last = store?.Events(descriptor.Id, null, null, 1).FirstOrDefault();
            var health = PluginHealth.Describe(last, []).Text;
            lines.Add(new GlanceLine(descriptor.Id, descriptor.Name,
                health.Length > 0 ? health : text["glance.nothing"], IsTrouble: false, FromPlugin: false));
        }

        return lines;
    }

    /// <summary>One plugin per line, names in a column, a <c>!</c> in front of whatever wants doing.</summary>
    public static string AsText(IReadOnlyList<GlanceLine> lines)
    {
        if (lines.Count == 0)
            return MeowsText.Current["glance.none"];

        var width = lines.Max(l => l.Name.Length);
        return string.Join(Environment.NewLine,
            lines.Select(l => $"{(l.IsTrouble ? "!" : " ")} {l.Name.PadRight(width)}  {l.Text}"));
    }

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        // Umlauts and emoji as themselves: whatever reads this reads UTF-8.
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    /// <summary>
    /// The same, for a program: when it was asked, whether anything wants doing, and each line
    /// with the plugin's id so a status bar can key on it rather than on a translated name.
    /// </summary>
    public static string AsJson(IReadOnlyList<GlanceLine> lines, DateTimeOffset at) =>
        JsonSerializer.Serialize(new
        {
            At = at,
            Trouble = lines.Any(l => l.IsTrouble),
            Plugins = lines,
        }, Json);
}
