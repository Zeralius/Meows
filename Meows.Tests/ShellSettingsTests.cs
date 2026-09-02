using Meows.Services;

namespace Meows.Tests;

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

    [Fact]
    public void Settings_from_the_old_name_are_carried_over_on_first_run()
    {
        var old = Path.Combine(_root, "Mews");
        var wanted = Path.Combine(_root, "Meows");

        Directory.CreateDirectory(Path.Combine(old, "plugins", "mews.chonk"));
        File.WriteAllText(Path.Combine(old, "activated-plugins.json"), "[\"mews.chonk\"]");
        File.WriteAllText(Path.Combine(old, "plugins", "mews.chonk", "settings.json"), "{}");

        var store = new ShellSettings(wanted, old);

        Assert.True(File.Exists(Path.Combine(wanted, "activated-plugins.json")));
        // The plugin ids moved with the name, and a plugin's folder is named after its id.
        Assert.True(Directory.Exists(Path.Combine(wanted, "plugins", "meows.chonk")));
        Assert.True(File.Exists(Path.Combine(wanted, "plugins", "meows.chonk", "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(wanted, "plugins", "mews.chonk")));
        Assert.Contains("carried over", store.StartupNote);
    }

    [Fact]
    public void The_plugins_that_were_switched_on_stay_switched_on()
    {
        var old = Path.Combine(_root, "Mews");
        var wanted = Path.Combine(_root, "Meows");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "activated-plugins.json"), "[\"mews.kibble\",\"mews.chonk\"]");

        _ = new ShellSettings(wanted, old);

        // The ids carry the app's name, so copying the file across unchanged would leave every
        // plugin switched off and look like the move lost them.
        var activated = File.ReadAllText(Path.Combine(wanted, "activated-plugins.json"));
        Assert.Contains("meows.kibble", activated);
        Assert.Contains("meows.chonk", activated);
        Assert.DoesNotContain("\"mews.", activated);
    }

    [Fact]
    public void The_old_log_is_not_dragged_along()
    {
        var old = Path.Combine(_root, "Mews");
        var wanted = Path.Combine(_root, "Meows");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "activated-plugins.json"), "[]");
        File.WriteAllText(Path.Combine(old, "mews.log"), "old history");

        _ = new ShellSettings(wanted, old);

        // It is history under the old name rather than settings, and two logs side by side would
        // leave neither of them the whole story.
        Assert.False(File.Exists(Path.Combine(wanted, "mews.log")));
        Assert.True(File.Exists(Path.Combine(wanted, "activated-plugins.json")));
    }

    [Fact]
    public void The_old_folder_is_copied_rather_than_moved()
    {
        var old = Path.Combine(_root, "Mews");
        var wanted = Path.Combine(_root, "Meows");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "activated-plugins.json"), "[]");

        _ = new ShellSettings(wanted, old);

        // If any of this went wrong the original is still there to go back to, which matters
        // more than tidiness for something that runs once.
        Assert.True(File.Exists(Path.Combine(old, "activated-plugins.json")));
    }

    [Fact]
    public void An_existing_folder_is_never_written_over_by_the_move()
    {
        var old = Path.Combine(_root, "Mews");
        var wanted = Path.Combine(_root, "Meows");
        Directory.CreateDirectory(old);
        File.WriteAllText(Path.Combine(old, "activated-plugins.json"), "[\"from the old one\"]");
        Directory.CreateDirectory(wanted);
        File.WriteAllText(Path.Combine(wanted, "activated-plugins.json"), "[\"already here\"]");

        var store = new ShellSettings(wanted, old);

        // Someone who has already run this version has real settings. Carrying the old ones over
        // on top of them would lose whatever they have done since.
        Assert.Contains("already here", File.ReadAllText(Path.Combine(wanted, "activated-plugins.json")));
        Assert.Null(store.StartupNote);
    }

    [Fact]
    public void An_empty_folder_does_not_count_as_already_set_up()
    {
        var old = Path.Combine(_root, "Mews");
        var wanted = Path.Combine(_root, "Meows");
        Directory.CreateDirectory(Path.Combine(old, "plugins", "mews.chonk"));
        File.WriteAllText(Path.Combine(old, "activated-plugins.json"), "[]");

        // Exactly what the crash log leaves behind: the folder made, with a log in it, before
        // any of the settings code runs. Reading that as "already set up" skipped the whole move.
        Directory.CreateDirectory(wanted);
        File.WriteAllText(Path.Combine(wanted, "meows.log"), "started");

        var store = new ShellSettings(wanted, old);

        Assert.True(File.Exists(Path.Combine(wanted, "activated-plugins.json")));
        Assert.Contains("carried over", store.StartupNote);
    }

    [Fact]
    public void Nothing_to_carry_over_is_not_an_event()
    {
        var store = new ShellSettings(Path.Combine(_root, "Meows"), Path.Combine(_root, "no old folder"));

        Assert.Null(store.StartupNote);
    }

}
