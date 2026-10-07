using System.IO.Compression;
using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// Sharing one plugin's settings: a zip with a manifest and the settings as they stand, and
/// back again with the current file kept aside. Secrets never travel; they are sealed beside
/// the settings, not in them.
/// </summary>
public sealed class ShareTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-share-" + Guid.NewGuid().ToString("N")[..10]);

    public ShareTests() => Directory.CreateDirectory(_root);

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

    private string SettingsDir(string root, string pluginId)
    {
        var dir = Path.Combine(root, "plugins", pluginId);
        Directory.CreateDirectory(dir);
        return dir;
    }

    [Fact]
    public void Export_names_the_plugin_and_holds_its_settings_as_they_stand()
    {
        var from = Path.Combine(_root, "from");
        var dir = SettingsDir(from, "meows.basket");
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{"lists":[{"title":"To do"}]}""");
        var zip = Path.Combine(_root, "basket.meows-share.zip");

        PluginShare.Export(from, "meows.basket", "Basket", "5.0.0", zip);

        using var archive = ZipFile.OpenRead(zip);
        Assert.Equal(["manifest.json", "settings.json"],
            archive.Entries.Select(e => e.FullName).OrderBy(n => n));
    }

    [Fact]
    public void Exporting_what_keeps_nothing_says_so()
    {
        var from = Path.Combine(_root, "empty");
        Directory.CreateDirectory(from);

        var error = Assert.Throws<InvalidOperationException>(() =>
            PluginShare.Export(from, "meows.basket", "Basket", "5.0.0", Path.Combine(_root, "x.zip")));

        Assert.Contains("nothing to share", error.Message);
    }

    [Fact]
    public void Import_puts_shared_settings_where_the_plugin_reads_them_and_keeps_what_was_there()
    {
        var from = Path.Combine(_root, "from");
        var dir = SettingsDir(from, "meows.collar");
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{"entries":[{"title":"Shared"}]}""");
        var zip = Path.Combine(_root, "collar.meows-share.zip");
        PluginShare.Export(from, "meows.collar", "Collar", "5.0.0", zip);

        var to = Path.Combine(_root, "to");
        var toDir = SettingsDir(to, "meows.collar");
        File.WriteAllText(Path.Combine(toDir, "settings.json"), """{"entries":[{"title":"Mine"}]}""");

        var report = PluginShare.Import(to, zip);

        Assert.Equal("meows.collar", report.PluginId);
        Assert.Equal("Collar", report.Name);
        Assert.True(report.BackedUp);
        Assert.Contains("Shared", File.ReadAllText(Path.Combine(toDir, "settings.json")));
        Assert.Contains("Mine", File.ReadAllText(Path.Combine(toDir, PluginShare.BackupName)));
    }

    [Fact]
    public void Importing_onto_nothing_keeps_nothing_aside()
    {
        var from = Path.Combine(_root, "from2");
        var dir = SettingsDir(from, "meows.basket");
        File.WriteAllText(Path.Combine(dir, "settings.json"), """{"lists":[]}""");
        var zip = Path.Combine(_root, "basket.meows-share.zip");
        PluginShare.Export(from, "meows.basket", "Basket", "5.0.0", zip);

        var report = PluginShare.Import(Path.Combine(_root, "to2"), zip);

        Assert.False(report.BackedUp);
    }

    [Fact]
    public void Anything_but_a_shared_zip_is_refused_with_the_reason()
    {
        var plain = Path.Combine(_root, "plain.zip");
        using (var zip = ZipFile.Open(plain, ZipArchiveMode.Create))
        {
            var entry = zip.CreateEntry("something.txt");
            using var writer = new StreamWriter(entry.Open());
            writer.Write("not shared settings");
        }

        var error = Assert.Throws<InvalidOperationException>(() =>
            PluginShare.Import(Path.Combine(_root, "to3"), plain));
        Assert.Contains("manifest", error.Message);

        var missing = Assert.Throws<InvalidOperationException>(() =>
            PluginShare.Import(Path.Combine(_root, "to3"), Path.Combine(_root, "missing.zip")));
        Assert.Contains("could not be read", missing.Message);
    }

    [Fact]
    public void Settings_that_do_not_parse_are_left_alone()
    {
        var zip = Path.Combine(_root, "broken.meows-share.zip");
        using (var archive = ZipFile.Open(zip, ZipArchiveMode.Create))
        {
            var manifest = archive.CreateEntry(PluginShare.ManifestName);
            using (var writer = new StreamWriter(manifest.Open()))
                writer.Write("""{"PluginId":"meows.basket","Name":"Basket","AppVersion":"5.0.0","ExportedUtc":"2026-10-05T00:00:00Z"}""");
            var settings = archive.CreateEntry(PluginShare.SettingsName);
            using (var writer = new StreamWriter(settings.Open()))
                writer.Write("{not json");
        }

        var error = Assert.Throws<InvalidOperationException>(() =>
            PluginShare.Import(Path.Combine(_root, "to4"), zip));
        Assert.Contains("do not parse", error.Message);
    }
}
