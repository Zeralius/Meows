using Avalonia.Controls;
using Meows.Plugins;
using Meows.Plugins.Abstractions;
using Meows.Services;
using Meows.ViewModels;

namespace Meows.Tests;

/// <summary>
/// A plugin written by someone else, against the contract and nothing more: no string catalogue,
/// no German, no second name, no icon, no category. Everything optional is optional, and the
/// shell shows what it was given rather than dotted identifiers or a blank.
/// </summary>
public sealed class BarePluginTests : IDisposable
{
    /// <summary>The least a plugin can be and still compile.</summary>
    internal sealed class Bare : IMeowsPlugin
    {
        public string Id => "someone.weather";

        public string DisplayName => "Weather Watch";

        public string Description => "Shows the weather.";

        public string? Icon => null;

        public Control CreateView(IMeowsHost host) => new Panel();
    }

    public void Dispose()
    {
        PluginNames.Feline = true;
        TestStrings.Install();
    }

    [Fact]
    public void A_description_that_is_not_a_key_is_shown_as_written_in_every_language()
    {
        var card = new PluginEntryViewModel(PluginDescriptor.Loaded(new Bare(), @"C:\plugins\Weather\Weather.dll"), (_, _) => { });

        Assert.Equal("Shows the weather.", card.Description);

        var german = TestStrings.Load();
        german.Use("de");
        MeowsText.Use(german);

        Assert.Equal("Shows the weather.", card.Description);
    }

    [Fact]
    public void A_plugin_with_no_second_name_reads_the_same_either_side_of_the_paw()
    {
        var plugin = new Bare();
        PluginNames.Register(plugin);
        var descriptor = PluginDescriptor.Loaded(plugin, @"C:\plugins\Weather\Weather.dll");
        var card = new PluginEntryViewModel(descriptor, (_, _) => { });

        PluginNames.Feline = true;
        Assert.Equal("Weather Watch", card.DisplayName);
        Assert.False(card.HasOtherName);

        PluginNames.Feline = false;
        Assert.Equal("Weather Watch", card.DisplayName);
        Assert.False(card.HasOtherName);
        Assert.Equal("Weather Watch", PluginNames.Display("Weather Watch"));
    }

    [Fact]
    public void No_icon_and_no_category_get_the_defaults_rather_than_a_hole()
    {
        var descriptor = PluginDescriptor.Loaded(new Bare(), @"C:\plugins\Weather\Weather.dll");

        Assert.Equal("●", descriptor.Icon);
        Assert.Null(descriptor.Category);
        Assert.True(descriptor.IsCompatible);
    }

    [Fact]
    public void Strings_the_plugin_asks_for_without_a_catalogue_come_back_as_written()
    {
        // What {m:Tr} and host.Text[...] do for a plugin that never shipped a catalogue: the
        // text it wrote is the text it gets, in English and in German alike.
        var text = TestStrings.Load();
        text.Use("de");

        Assert.Equal("Refresh now", text["Refresh now"]);
        Assert.Equal("Loaded 3 things", text.Format("Loaded {0} things", 3));
    }

    [Fact]
    public void A_broken_catalogue_is_reported_and_does_not_stop_the_rest_loading()
    {
        // The test assembly carries Strings.zz.json, which is not JSON. Adding the assembly has
        // to say so and still read the two good catalogues beside it.
        var complaints = new List<string>();
        var text = new Translations(complaints.Add);

        text.Add(typeof(BarePluginTests).Assembly);

        Assert.Contains(complaints, c => c.Contains("Strings.zz.json"));
        Assert.Equal("Only in English", text["test.englishonly"]);
        text.Use("de");
        Assert.Equal("Will {0}", text["test.needsone"]);
    }
}
