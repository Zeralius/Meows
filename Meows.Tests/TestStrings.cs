using System.Reflection;
using System.Runtime.CompilerServices;
using Meows.Plugins.Abstractions;
using Meows.Services;

// One test switches the process-wide language to check that bindings hear about it, and every
// other test in here reads English. Running classes side by side would let that switch land in
// the middle of one of them. The whole suite takes about two seconds, so serialising it is a
// cheaper answer than a lock nobody remembers to take.
[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace Meows.Tests;

/// <summary>
/// Puts the English catalogue in place before any test runs.
///
/// Plenty of these tests assert on the words somebody would actually read, and those words now
/// live in Strings.en.json rather than in the code. Without this they would all be comparing
/// against bare keys. It also means a key nobody remembered to add fails a test here rather than
/// only turning up as a dotted identifier on screen.
/// </summary>
internal static class TestStrings
{
    /// <summary>
    /// The shell's own strings plus every plugin's, from the one list the catalogue tests guard,
    /// so a plugin added there is loaded here too.
    /// </summary>
    private static IEnumerable<Assembly> Everything =>
        [typeof(Translations).Assembly, .. CatalogueTests.Carriers];

    /// <summary>A fresh table holding everything, for tests that want to poke at it directly.</summary>
    internal static Translations Load()
    {
        var text = new Translations();
        foreach (var assembly in Everything)
            text.Add(assembly);
        return text;
    }

    [ModuleInitializer]
    internal static void Install()
    {
        MeowsText.Use(Load());

        // The shared bot location lives under the real %APPDATA% by default. A test that picks
        // a bot folder must not write that into the file the running app reads.
        Meows.Bot.BotLocation.SettingsFolder =
            Path.Combine(Path.GetTempPath(), "meows-tests-shared-" + Guid.NewGuid().ToString("N"));
    }
}
