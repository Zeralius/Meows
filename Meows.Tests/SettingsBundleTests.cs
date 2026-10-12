using System.IO.Compression;
using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// The settings folder as one zip and back: everything travels except secrets and the log, an
/// import keeps what it writes over, and a bundle cannot write outside the folder.
/// </summary>
public sealed class SettingsBundleTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "bundle-" + Guid.NewGuid().ToString("N")[..10]);

    public SettingsBundleTests() => Directory.CreateDirectory(_root);

    private string Settings(string name) => Path.Combine(_root, "settings-" + name);

    private void Populate(string root)
    {
        Directory.CreateDirectory(Path.Combine(root, "plugins", "meows.kibble"));
        Directory.CreateDirectory(Path.Combine(root, "plugins", "meows.scruff", "secrets"));
        File.WriteAllText(Path.Combine(root, "preferences.json"), "{\"theme\":\"dark\"}");
        File.WriteAllText(Path.Combine(root, "activated-plugins.json"), "[\"meows.kibble\"]");
        File.WriteAllText(Path.Combine(root, "meows.db"), "db");
        File.WriteAllText(Path.Combine(root, "meows.log"), "log");
        File.WriteAllText(Path.Combine(root, "plugins", "meows.kibble", "settings.json"), "{\"pageSize\":50}");
        File.WriteAllText(Path.Combine(root, "plugins", "meows.scruff", "secrets", "bluesky.secret"), "SEALED");
    }

    [Fact]
    public void Everything_travels_except_secrets_and_the_log()
    {
        var root = Settings("a");
        Populate(root);
        var zip = Path.Combine(_root, "out.zip");

        var report = SettingsBundle.Export(root, zip, "9.9.9");

        Assert.True(report.Ok);
        Assert.Equal(4, report.Files);
        using var archive = ZipFile.OpenRead(zip);
        var names = archive.Entries.Select(e => e.FullName).ToList();
        Assert.Contains("preferences.json", names);
        Assert.Contains("plugins/meows.kibble/settings.json", names);
        Assert.Contains(SettingsBundle.Manifest, names);
        Assert.DoesNotContain("meows.log", names);
        Assert.DoesNotContain(names, n => n.Contains("secrets"));
    }

    [Fact]
    public void An_import_lands_the_files_and_keeps_what_was_there_first()
    {
        var from = Settings("from");
        Populate(from);
        var zip = Path.Combine(_root, "move.zip");
        SettingsBundle.Export(from, zip, "9.9.9");

        var to = Settings("to");
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(to, "preferences.json"), "{\"theme\":\"light\"}");

        var report = SettingsBundle.Import(to, zip, "9.9.9");

        Assert.True(report.Ok);
        Assert.Equal(4, report.Files);
        Assert.Equal("{\"theme\":\"dark\"}", File.ReadAllText(Path.Combine(to, "preferences.json")));
        Assert.Equal("{\"pageSize\":50}", File.ReadAllText(Path.Combine(to, "plugins", "meows.kibble", "settings.json")));
        Assert.False(Directory.Exists(Path.Combine(to, "plugins", "meows.scruff", "secrets")));

        // The light theme is in the backup, which is itself a bundle.
        Assert.NotNull(report.BackupPath);
        using var backup = ZipFile.OpenRead(report.BackupPath!);
        using var reader = new StreamReader(backup.GetEntry("preferences.json")!.Open());
        Assert.Equal("{\"theme\":\"light\"}", reader.ReadToEnd());

        // And the backup does not travel in the next export.
        var again = Path.Combine(_root, "again.zip");
        SettingsBundle.Export(to, again, "9.9.9");
        using var second = ZipFile.OpenRead(again);
        Assert.DoesNotContain(second.Entries, e => e.FullName.StartsWith("before-import-"));
    }

    [Fact]
    public void Secrets_in_a_bundle_and_paths_that_escape_are_never_written()
    {
        var zip = Path.Combine(_root, "hostile.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var m = new StreamWriter(archive.CreateEntry(SettingsBundle.Manifest).Open()))
                m.Write("{}");
            using (var s = new StreamWriter(archive.CreateEntry("plugins/meows.scruff/secrets/bluesky.secret").Open()))
                s.Write("STOLEN");
            using (var s = new StreamWriter(archive.CreateEntry("../outside.txt").Open()))
                s.Write("escaped");
            using (var s = new StreamWriter(archive.CreateEntry("preferences.json").Open()))
                s.Write("{}");
        }

        var to = Settings("hostile");
        Directory.CreateDirectory(to);
        var report = SettingsBundle.Import(to, zip, "9.9.9");

        Assert.True(report.Ok);
        Assert.Equal(1, report.Files);
        Assert.Equal(2, report.Skipped);
        Assert.False(File.Exists(Path.Combine(to, "plugins", "meows.scruff", "secrets", "bluesky.secret")));
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
    }

    [Fact]
    public void Something_that_is_not_a_bundle_is_refused_before_anything_is_touched()
    {
        var zip = Path.Combine(_root, "plain.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        using (var s = new StreamWriter(archive.CreateEntry("preferences.json").Open()))
            s.Write("{}");

        var to = Settings("refused");
        Directory.CreateDirectory(to);
        var report = SettingsBundle.Import(to, zip, "9.9.9");

        Assert.False(report.Ok);
        Assert.Contains("manifest", report.Error);
        Assert.Empty(Directory.GetFiles(to));
    }

    [Fact]
    public void An_import_waits_for_the_next_start_and_nothing_is_written_until_then()
    {
        var from = Settings("staged-from");
        Populate(from);
        var zip = Path.Combine(_root, "staged.zip");
        SettingsBundle.Export(from, zip, "9.9.9");

        var to = Settings("staged-to");
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(to, "preferences.json"), "{\"theme\":\"light\"}");

        var staged = SettingsBundle.Stage(to, zip);

        Assert.True(staged.Ok);
        Assert.Equal(4, staged.Files);
        Assert.Equal("{\"theme\":\"light\"}", File.ReadAllText(Path.Combine(to, "preferences.json")));

        var applied = SettingsBundle.ApplyPending(to, "9.9.9");

        Assert.NotNull(applied);
        Assert.True(applied!.Ok);
        Assert.Contains("dark", File.ReadAllText(Path.Combine(to, "preferences.json")));
        Assert.False(File.Exists(Path.Combine(to, SettingsBundle.Pending)));
        Assert.Null(SettingsBundle.ApplyPending(to, "9.9.9"));
    }

    [Fact]
    public void A_bundle_cannot_point_this_machine_at_another_server_or_bring_rules_that_act_unseen()
    {
        var zip = Path.Combine(_root, "theirs.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            using (var m = new StreamWriter(archive.CreateEntry(SettingsBundle.Manifest).Open()))
                m.Write("{}");
            using (var p = new StreamWriter(archive.CreateEntry("preferences.json").Open()))
                p.Write("""
                        {"theme":"dark",
                         "reach":{"kind":"sftp","host":"their.server","hostKey":"THEIRS"},
                         "rules":[{"id":"mine","enabled":true},{"id":"theirs","enabled":true}]}
                        """);
        }

        var to = Settings("server");
        Directory.CreateDirectory(to);
        File.WriteAllText(Path.Combine(to, "preferences.json"), """
                                                               {"theme":"light",
                                                                "reach":{"kind":"folder","folder":"Z:\\"},
                                                                "rules":[{"id":"mine","enabled":true}]}
                                                               """);

        SettingsBundle.Stage(to, zip);
        var applied = SettingsBundle.ApplyPending(to, "9.9.9");

        var after = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(Path.Combine(to, "preferences.json")))!;
        Assert.Equal("dark", (string?)after["theme"]);
        Assert.Equal("folder", (string?)after["reach"]!["kind"]);
        Assert.Null(after["reach"]!["hostKey"]);
        var rules = after["rules"]!.AsArray();
        Assert.True((bool)rules.Single(r => (string?)r!["id"] == "mine")!["enabled"]!);
        Assert.False((bool)rules.Single(r => (string?)r!["id"] == "theirs")!["enabled"]!);
        Assert.Equal(1, applied!.RulesSwitchedOff);
    }

    [Theory]
    [InlineData("meows.db-journal")]
    [InlineData("meows.db-wal")]
    [InlineData("meows.db-shm")]
    [InlineData("plugins/meows.kibble/settings.json.writing")]
    [InlineData("plugins/meows.tin/accounts.private.json")]
    public void What_belongs_to_one_open_database_or_one_unfinished_save_or_is_private_does_not_travel(string name)
    {
        Assert.False(SettingsBundle.Belongs(name));
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
