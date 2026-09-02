using Mews.Services;

namespace Mews.Tests;

public sealed class ShellSettingsTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "settings-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly List<string> _reported = [];

    private ShellSettings Store()
    {
        var settings = new ShellSettings(_root);
        settings.Report = _reported.Add;
        return settings;
    }

    private sealed class Kept
    {
        public string? Where { get; set; }

        public bool Ask { get; set; } = true;
    }

    private string SettingsFile(string pluginId) =>
        Path.Combine(_root, "plugins", pluginId, "settings.json");

    [Fact]
    public void What_was_saved_comes_back()
    {
        Store().SavePluginSettings("test.plugin", new Kept { Where = @"E:\", Ask = false });

        var back = Store().LoadPluginSettings<Kept>("test.plugin");

        Assert.Equal(@"E:\", back!.Where);
        Assert.False(back.Ask);
    }

    [Fact]
    public void An_unreadable_settings_file_is_kept_rather_than_thrown_away()
    {
        var store = Store();
        store.SavePluginSettings("test.plugin", new Kept { Where = @"E:\" });
        var file = SettingsFile("test.plugin");

        // Truncated, which is what a crash partway through a write leaves behind.
        File.WriteAllText(file, "{\"where\": \"E:\\\\\", \"as");

        Assert.Null(store.LoadPluginSettings<Kept>("test.plugin"));

        // The original is still on disk under another name, so nothing is lost for good.
        var kept = Directory.GetFiles(Path.GetDirectoryName(file)!, "settings.json.unreadable-*");
        Assert.Single(kept);
        Assert.Contains("as", File.ReadAllText(kept[0]));
    }

    [Fact]
    public void Being_unable_to_read_a_settings_file_is_said_out_loud()
    {
        var store = Store();
        store.SavePluginSettings("test.plugin", new Kept());
        File.WriteAllText(SettingsFile("test.plugin"), "not json at all");

        store.LoadPluginSettings<Kept>("test.plugin");

        // Silence was the bug. A plugin quietly reverting to defaults looks like it forgot.
        Assert.Contains(_reported, r => r.Contains("could not be read"));
    }

    [Fact]
    public void A_later_save_cannot_overwrite_the_file_it_failed_to_read()
    {
        var store = Store();
        store.SavePluginSettings("test.plugin", new Kept { Where = @"E:\" });
        var file = SettingsFile("test.plugin");
        File.WriteAllText(file, "{ broken");

        // Exactly the sequence that lost the lot: read fails, plugin starts on defaults, the
        // first change anyone makes writes those defaults over the top.
        store.LoadPluginSettings<Kept>("test.plugin");
        store.SavePluginSettings("test.plugin", new Kept());

        var kept = Directory.GetFiles(Path.GetDirectoryName(file)!, "settings.json.unreadable-*");
        Assert.Single(kept);
        Assert.Equal("{ broken", File.ReadAllText(kept[0]));
    }

    [Fact]
    public void A_settings_file_is_never_left_half_written()
    {
        var store = Store();
        store.SavePluginSettings("test.plugin", new Kept { Where = @"E:\" });
        store.SavePluginSettings("test.plugin", new Kept { Where = @"F:\" });

        var directory = Path.GetDirectoryName(SettingsFile("test.plugin"))!;

        // The temporary file is the whole mechanism, so it must not survive the write.
        Assert.Empty(Directory.GetFiles(directory, "*.writing"));
        Assert.Equal(@"F:\", store.LoadPluginSettings<Kept>("test.plugin")!.Where);
    }

    [Fact]
    public void An_unreadable_activation_list_does_not_stop_the_app_starting()
    {
        var store = Store();
        File.WriteAllText(Path.Combine(_root, "activated-plugins.json"), "[ truncated");

        var activated = store.LoadActivatedPlugins();

        Assert.Empty(activated);
        Assert.Contains(_reported, r => r.Contains("could not be read"));
        Assert.Single(Directory.GetFiles(_root, "activated-plugins.json.unreadable-*"));
    }

    [Fact]
    public void A_missing_settings_file_is_not_a_problem_worth_reporting()
    {
        Assert.Null(Store().LoadPluginSettings<Kept>("never.saved"));

        // Nothing has gone wrong here, and saying so every launch would be noise.
        Assert.Empty(_reported);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }
}
