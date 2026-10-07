using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Meows.Plugins.Abstractions;

namespace Meows.Tests;

/// <summary>
/// Every plugin that ships says something on its Home card, or has decided it has nothing to
/// say yet. A rule rather than a pass over the plugins: <see cref="ShippedPlugins"/> is walked,
/// so a plugin added tomorrow without a glance fails here, and Kitten writes one into every
/// plugin it makes so that it does not have to.
///
/// Implementing <see cref="IGlanceable"/> is what is enforced, not a line of text. A plugin that
/// has looked at nothing yet has no news and answers null, which Home reads as "fall back to what
/// the shell can say for you"; that is the right answer on a fresh start, not a failure.
/// </summary>
public sealed class GlanceSystemTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-glance-system-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    public static TheoryData<string> EveryPlugin()
    {
        var data = new TheoryData<string>();
        foreach (var type in ShippedPlugins.Types)
            data.Add(type.FullName!);
        return data;
    }

    [AvaloniaTheory]
    [MemberData(nameof(EveryPlugin))]
    public void Every_plugin_that_ships_answers_the_home_card(string typeName)
    {
        var type = ShippedPlugins.Types.Single(t => t.FullName == typeName);
        var plugin = (IMeowsPlugin)Activator.CreateInstance(type)!;
        var host = new FakeHost(Path.Combine(_root, plugin.Id));

        var view = plugin.CreateView(host);
        try
        {
            var model = view.DataContext ?? view;
            var glanceable = Assert.IsAssignableFrom<IGlanceable>(model);

            // Asked the way Home asks: on a view model that has just been built and has not been
            // shown anything. It may have nothing to say; it may not throw.
            var glance = glanceable.Glance();

            // A glance with no words would draw an empty line on the card. Null is how "nothing"
            // is said.
            if (glance is not null)
                Assert.False(string.IsNullOrWhiteSpace(glance.Text), $"{plugin.DisplayName} answered with an empty line.");
        }
        finally
        {
            (view.DataContext as IDisposable)?.Dispose();
            (view as IDisposable)?.Dispose();
        }
    }
}
