using System.ComponentModel;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Molt.ViewModels;
using Meows.Plugins.Purrge.ViewModels;
using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// What happens to a tab that is already open when the language changes.
///
/// This is the half that was missed the first time. A view bound with {m:Tr} looks after itself,
/// because that is a binding to a string that announces when it changed. Anything a view model
/// works out in code is not: the property would return the new language perfectly well, but
/// nothing asks it to, so the tab keeps showing whatever it read when it opened. Switching to
/// English left half of Mouser in German and it looked like the setting had not taken.
///
/// These run with the shared string table swapped out, and put it back afterwards, because the
/// whole suite reads English.
/// </summary>
public class LanguageChangeTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "meows-language-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        TestStrings.Install();
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>Swaps in a table set to German and hands it back so a test can switch it.</summary>
    private static Translations SpeakingGerman()
    {
        var text = TestStrings.Load();
        text.Use("de");
        MeowsText.Use(text);
        return text;
    }

    [Fact]
    public void A_watch_hears_the_change()
    {
        var told = 0;
        using var watch = new LanguageWatch(() => told++);

        SpeakingGerman();

        Assert.True(told > 0);
    }

    [Fact]
    public void A_watch_that_has_been_let_go_hears_nothing()
    {
        // The string table outlives every plugin, so a view model that forgets to release this
        // stays alive through it along with the whole tab hanging off it.
        var told = 0;
        var watch = new LanguageWatch(() => told++);
        watch.Dispose();

        SpeakingGerman();

        Assert.Equal(0, told);
    }

    [Fact]
    public void An_open_tab_re_reads_the_text_it_worked_out_itself()
    {
        var host = new FakeHost(_root);
        using var model = new MoltViewModel(host);

        // Bound in the view, worked out in the view model, and English to begin with.
        Assert.Equal("Nothing picked", model.PickedText);
        Assert.StartsWith("Sent to the Recycle Bin", model.ModeText);

        var changed = new List<string?>();
        model.PropertyChanged += (_, e) => changed.Add(e.PropertyName);

        SpeakingGerman();

        Assert.Equal("Nichts ausgewählt", model.PickedText);
        Assert.StartsWith("Geht in den Papierkorb", model.ModeText);

        // A null name is the long standing way of saying every property may read differently,
        // which saves the view model listing its own text properties and getting that list
        // wrong the first time somebody adds one.
        Assert.Contains(null, changed);
    }

    [Fact]
    public void The_opening_line_follows_the_language_too()
    {
        // It used to be captured into a field when the plugin was switched on, so it was stuck
        // in whatever language the window happened to be in at that moment. This is the exact
        // line that stayed German underneath the tabs.
        //
        // Purrge rather than Molt, because Molt starts measuring the moment it opens and has
        // said something real before anyone can read the greeting.
        var host = new FakeHost(_root);
        using var model = new PurrgeViewModel(host);

        Assert.Equal("Pick a folder on the left, then scan.", model.StatusMessage);

        SpeakingGerman();

        Assert.Equal("Links einen Ordner wählen, dann suchen.", model.StatusMessage);
    }

    [Fact]
    public void Something_that_already_happened_is_left_as_it_was_said()
    {
        // Only the opening line moves. A message about something that already happened was
        // written in the language of the moment, and re-translating it would be rewriting
        // history, quite apart from being impossible once numbers are formatted into it.
        var host = new FakeHost(_root);
        using var model = new MoltViewModel(host);

        model.Show([]);
        Assert.Equal("Nothing to shed.", model.Status);

        SpeakingGerman();

        Assert.Equal("Nothing to shed.", model.Status);
    }

    [Fact]
    public void A_plugin_switched_on_while_the_window_is_German_opens_in_German()
    {
        // The other half of the same bug. The greeting was worked out once, so a plugin
        // activated after a language change was correct and one activated before it was not.
        SpeakingGerman();

        var host = new FakeHost(_root);
        using var model = new PurrgeViewModel(host);

        Assert.Equal("Links einen Ordner wählen, dann suchen.", model.StatusMessage);
    }

    [Fact]
    public void Every_plugin_view_model_holds_a_watch()
    {
        // The fix is one line per plugin and the sort of line that is easy to leave out of the
        // next one. Rather than trusting that, ask the assemblies.
        var missing = new List<string>();
        var checked_ = 0;

        foreach (var assembly in new[]
                 {
                     typeof(Plugins.Chonk.ChonkPlugin).Assembly,
                     typeof(Plugins.Kibble.KibblePlugin).Assembly,
                     typeof(Plugins.Litter.LitterPlugin).Assembly,
                     typeof(Plugins.Molt.MoltPlugin).Assembly,
                     typeof(Plugins.Mouser.MouserPlugin).Assembly,
                     typeof(Plugins.Purrge.PurrgePlugin).Assembly,
                     typeof(Plugins.Saucer.SaucerPlugin).Assembly,
                     typeof(Plugins.TelegramPoster.TelegramPosterPlugin).Assembly,
                     typeof(Plugins.Birdwatch.BirdwatchPlugin).Assembly,
                 })
        {
            // The one the plugin hands the shell, which is the one whose text is on screen.
            var main = assembly.GetTypes().SingleOrDefault(t =>
                t.Name.EndsWith("ViewModel", StringComparison.Ordinal) &&
                t.Name.StartsWith(assembly.GetName().Name!.Split('.').Last(), StringComparison.Ordinal));

            Assert.NotNull(main);
            checked_++;

            var holds = main
                .GetFields(System.Reflection.BindingFlags.Instance |
                           System.Reflection.BindingFlags.NonPublic |
                           System.Reflection.BindingFlags.Public)
                .Any(f => f.FieldType == typeof(LanguageWatch));

            if (!holds)
                missing.Add(main.Name);
        }

        Assert.Empty(missing);

        // Guards the loop above. A type lookup that quietly finds nothing would pass this
        // test with no plugins examined at all.
        Assert.Equal(9, checked_);
    }
}
