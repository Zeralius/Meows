using System.IO.Compression;
using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// A plugin's zip into the plugins folder: named after the DLL whatever the zip was called,
/// with or without a folder inside, refusing what it cannot place, and waiting beside a plugin
/// that is already there rather than writing over one that is loaded.
/// </summary>
public sealed class PluginInstallerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "install-" + Guid.NewGuid().ToString("N")[..10]);
    private readonly string _plugins;

    public PluginInstallerTests()
    {
        _plugins = Path.Combine(_root, "plugins");
        Directory.CreateDirectory(_plugins);
    }

    private string Zip(string name, params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(_root, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        foreach (var (entryPath, content) in entries)
        {
            var entry = zip.CreateEntry(entryPath);
            using var writer = new StreamWriter(entry.Open());
            writer.Write(content);
        }
        return path;
    }

    private static (string, string)[] BuildOutput(string prefix, string name) =>
    [
        (prefix + name + ".dll", "plugin"),
        (prefix + name + ".deps.json", "{}"),
        (prefix + name + ".pdb", "symbols"),
        (prefix + "Meows.Disk.dll", "library"),
        (prefix + "Strings/Strings.en.json", "{}"),
    ];

    [Fact]
    public void A_zipped_folder_lands_under_its_dll_name()
    {
        var zip = Zip("WeatherWatch-1.0.zip", BuildOutput("WeatherWatch-1.0/", "WeatherWatch"));

        var report = PluginInstaller.Install(zip, _plugins);

        Assert.True(report.Ok, report.Error);
        Assert.Equal("WeatherWatch", report.Name);
        Assert.False(report.Pending);
        Assert.True(File.Exists(Path.Combine(_plugins, "WeatherWatch", "WeatherWatch.dll")));
        Assert.True(File.Exists(Path.Combine(_plugins, "WeatherWatch", "Strings", "Strings.en.json")));
        Assert.False(Directory.Exists(Path.Combine(_plugins, "WeatherWatch-1.0")));
        Assert.Empty(Directory.EnumerateDirectories(_plugins, "*.installing"));
    }

    [Fact]
    public void Zipped_files_with_no_folder_land_the_same_way()
    {
        var zip = Zip("files.zip", BuildOutput("", "WeatherWatch"));

        var report = PluginInstaller.Install(zip, _plugins);

        Assert.True(report.Ok, report.Error);
        Assert.Equal("WeatherWatch", report.Name);
        Assert.True(File.Exists(Path.Combine(_plugins, "WeatherWatch", "Meows.Disk.dll")));
    }

    [Fact]
    public void A_zip_with_backslashes_in_it_is_read_the_same_way()
    {
        // What Compress-Archive writes: the folder is there, but the separator is not a slash.
        var zip = Zip("windows.zip",
            (@"WeatherWatch\WeatherWatch.dll", "plugin"),
            (@"WeatherWatch\WeatherWatch.deps.json", "{}"),
            (@"WeatherWatch\Strings\Strings.en.json", "{}"));

        var report = PluginInstaller.Install(zip, _plugins);

        Assert.True(report.Ok, report.Error);
        Assert.Equal("WeatherWatch", report.Name);
        Assert.True(File.Exists(Path.Combine(_plugins, "WeatherWatch", "WeatherWatch.dll")));
        Assert.True(File.Exists(Path.Combine(_plugins, "WeatherWatch", "Strings", "Strings.en.json")));
        Assert.False(Directory.Exists(Path.Combine(_plugins, "WeatherWatch", "WeatherWatch")));
    }

    [Fact]
    public void One_dll_and_no_deps_file_is_still_unambiguous()
    {
        var zip = Zip("bare.zip", ("Hello/Hello.dll", "plugin"), ("Hello/readme.txt", "hi"));

        var report = PluginInstaller.Install(zip, _plugins);

        Assert.True(report.Ok, report.Error);
        Assert.Equal("Hello", report.Name);
    }

    [Fact]
    public void Several_dlls_and_no_deps_file_is_refused()
    {
        var zip = Zip("which.zip", ("A.dll", "a"), ("B.dll", "b"));

        var report = PluginInstaller.Install(zip, _plugins);

        Assert.False(report.Ok);
        Assert.Contains("which DLL", report.Error);
        Assert.Empty(Directory.EnumerateDirectories(_plugins));
    }

    [Fact]
    public void Not_a_zip_is_said_plainly()
    {
        var path = Path.Combine(_root, "plugin.zip");
        File.WriteAllText(path, "this is not a zip");

        var report = PluginInstaller.Install(path, _plugins);

        Assert.False(report.Ok);
        Assert.Equal("not a zip file", report.Error);
    }

    [Fact]
    public void An_entry_that_climbs_out_is_refused_and_nothing_is_left_behind()
    {
        var zip = Zip("slip.zip", ("Evil/Evil.dll", "plugin"), ("Evil/../../outside.txt", "escaped"));

        var report = PluginInstaller.Install(zip, _plugins);

        Assert.False(report.Ok);
        Assert.Contains("outside", report.Error);
        Assert.False(File.Exists(Path.Combine(_root, "outside.txt")));
        Assert.Empty(Directory.EnumerateDirectories(_plugins));
    }

    [Fact]
    public void A_second_install_waits_beside_the_first()
    {
        PluginInstaller.Install(Zip("v1.zip", BuildOutput("WeatherWatch/", "WeatherWatch")), _plugins);
        var v2 = Zip("v2.zip", BuildOutput("WeatherWatch/", "WeatherWatch").Append(("WeatherWatch/new.txt", "v2")).ToArray());

        var report = PluginInstaller.Install(v2, _plugins);

        Assert.True(report.Ok, report.Error);
        Assert.True(report.Pending);
        Assert.False(File.Exists(Path.Combine(_plugins, "WeatherWatch", "new.txt")));
        Assert.True(File.Exists(Path.Combine(_plugins, "WeatherWatch" + PluginInstaller.PendingSuffix, "new.txt")));
        Assert.True(PluginInstaller.IsTransient(Path.Combine(_plugins, "WeatherWatch" + PluginInstaller.PendingSuffix)));
    }

    [Fact]
    public void Pending_is_swapped_in_when_nothing_holds_the_old_one()
    {
        PluginInstaller.Install(Zip("v1.zip", BuildOutput("WeatherWatch/", "WeatherWatch")), _plugins);
        PluginInstaller.Install(Zip("v2.zip", BuildOutput("WeatherWatch/", "WeatherWatch").Append(("WeatherWatch/new.txt", "v2")).ToArray()), _plugins);

        var said = PluginInstaller.ApplyPending(_plugins);

        Assert.Single(said);
        Assert.Contains("Replaced WeatherWatch", said[0]);
        Assert.True(File.Exists(Path.Combine(_plugins, "WeatherWatch", "new.txt")));
        Assert.False(Directory.Exists(Path.Combine(_plugins, "WeatherWatch" + PluginInstaller.PendingSuffix)));
        Assert.False(PluginInstaller.IsWaiting(_plugins, "WeatherWatch"));
        Assert.Empty(Directory.EnumerateDirectories(_plugins, "*.old"));
    }

    [Fact]
    public void Pending_stays_when_the_old_one_is_held_open()
    {
        PluginInstaller.Install(Zip("v1.zip", BuildOutput("WeatherWatch/", "WeatherWatch")), _plugins);
        PluginInstaller.Install(Zip("v2.zip", BuildOutput("WeatherWatch/", "WeatherWatch")), _plugins);

        using (File.Open(Path.Combine(_plugins, "WeatherWatch", "WeatherWatch.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            var said = PluginInstaller.ApplyPending(_plugins);
            Assert.Single(said);
            Assert.Contains("still in use", said[0]);
        }

        Assert.True(PluginInstaller.IsWaiting(_plugins, "WeatherWatch"));

        Assert.True(Directory.Exists(Path.Combine(_plugins, "WeatherWatch" + PluginInstaller.PendingSuffix)));
        // Whole, not half deleted: the strings and the deps file are still beside the locked DLL.
        Assert.True(File.Exists(Path.Combine(_plugins, "WeatherWatch", "WeatherWatch.dll")));
        Assert.True(File.Exists(Path.Combine(_plugins, "WeatherWatch", "WeatherWatch.deps.json")));
        Assert.True(File.Exists(Path.Combine(_plugins, "WeatherWatch", "Strings", "Strings.en.json")));
    }

    [Fact]
    public void The_installer_leaves_its_note_and_reads_it_back()
    {
        PluginInstaller.Install(Zip("WeatherWatch-1.0.zip", BuildOutput("WeatherWatch/", "WeatherWatch")), _plugins, "https://example/release");

        var record = PluginInstaller.RecordOf(Path.Combine(_plugins, "WeatherWatch"));

        Assert.NotNull(record);
        Assert.Equal("WeatherWatch-1.0.zip", record.From);
        Assert.Equal("https://example/release", record.Source);
        Assert.True((DateTime.Now - record.On).Duration() < TimeSpan.FromMinutes(1));
        Assert.Null(PluginInstaller.RecordOf(_root));
    }

    [Fact]
    public void Uninstall_takes_the_folder_away()
    {
        PluginInstaller.Install(Zip("v1.zip", BuildOutput("WeatherWatch/", "WeatherWatch")), _plugins);
        var folder = Path.Combine(_plugins, "WeatherWatch");

        var error = PluginInstaller.Uninstall(folder);

        Assert.Null(error);
        Assert.False(Directory.Exists(folder));
        Assert.Empty(Directory.EnumerateDirectories(_plugins));
    }

    [Fact]
    public void Uninstall_of_a_folder_held_open_moves_it_aside_whole_or_not_at_all()
    {
        PluginInstaller.Install(Zip("v1.zip", BuildOutput("WeatherWatch/", "WeatherWatch")), _plugins);
        var folder = Path.Combine(_plugins, "WeatherWatch");

        string? error;
        using (File.Open(Path.Combine(folder, "WeatherWatch.dll"), FileMode.Open, FileAccess.Read, FileShare.None))
            error = PluginInstaller.Uninstall(folder);

        // Either the rename was refused and the folder is untouched, or it went aside and is
        // skipped by the scan until it can go. Never a half-deleted folder.
        var remaining = Directory.GetDirectories(_plugins).Select(Path.GetFileName).ToList();
        if (error is null)
        {
            Assert.Equal(["WeatherWatch.old"], remaining);
            Assert.True(File.Exists(Path.Combine(_plugins, "WeatherWatch.old", "Strings", "Strings.en.json")));
            Assert.True(PluginInstaller.IsTransient(Path.Combine(_plugins, "WeatherWatch.old")));
        }
        else
        {
            Assert.Equal(["WeatherWatch"], remaining);
            Assert.True(File.Exists(Path.Combine(folder, "Strings", "Strings.en.json")));
        }
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }
}
