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
