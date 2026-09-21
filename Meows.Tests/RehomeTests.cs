using Meows.Plugins.Rehome.Services;
using Meows.Plugins.Rehome.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Rehome: the program list is matched honestly, the copy proves it arrived and removes
/// nothing, the manifest says where everything came from, and the way back reads it.
/// </summary>
public sealed class RehomeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rehome-" + Guid.NewGuid().ToString("N")[..10]);

    public RehomeTests() => Directory.CreateDirectory(_root);

    private string Under(string relative, string? content = null)
    {
        var path = Path.Combine(_root, relative);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        if (content is not null)
            File.WriteAllText(path, content);
        else
            Directory.CreateDirectory(path);
        return path;
    }

    [Fact]
    public void Opens_with_something_to_say()
    {
        using var model = new RehomeViewModel(new FakeHost(Path.Combine(_root, "hostdata")));

        Assert.False(string.IsNullOrWhiteSpace(model.Status));
        Assert.False(model.HasError);
        Assert.Equal(5, model.Steps.Count);
        Assert.False(model.HasScanned);
    }

    [Fact]
    public void A_destination_on_a_wiped_drive_is_no_destination()
    {
        using var model = new RehomeViewModel(new FakeHost(Path.Combine(_root, "hostdata")));
        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!;

        model.SetDestination(Path.Combine(system, "Rehome-test"));

        Assert.False(model.CanPack);
        Assert.NotEqual("", model.DestinationWarning);
        Assert.True(Carry.OnWipedDrive(Path.Combine(system, "x"), [system.TrimEnd('\\')]));
        Assert.False(Carry.OnWipedDrive(@"Z:\x", [system.TrimEnd('\\')]));
    }

    [Fact]
    public void The_winget_table_is_read_by_its_header_and_only_winget_rows_count()
    {
        var lines = new[]
        {
            "Die Quelle \"msstore\" erfordert, dass Sie die folgenden Vereinbarungen vor der Verwendung anzeigen.",
            "   -",
            "Name                          ID                        Version      Verfügbar   Quelle",
            "-----------------------------------------------------------------------------------------",
            "7-Zip 24.08 (x64)             7zip.7zip                 24.08                    winget",
            "Discord                       Discord.Discord           1.0.9165     1.0.9170    winget",
            "Some Driver Thing             ARP\\Machine\\X64\\{GUID}    1.2",
            "A Very Long Program Name Tha… Publisher.LongProgram     3.0                      winget",
        };

        var found = PackageManagers.ParseWingetTable(lines);

        Assert.Equal(3, found.Count);
        Assert.Equal("7zip.7zip", found[0].Id);
        Assert.Equal("7-Zip 24.08 (x64)", found[0].Name);
        Assert.Equal("24.08", found[0].Version);
        Assert.Equal("Discord.Discord", found[1].Id);
        Assert.Equal("1.0.9165", found[1].Version);
    }

    [Fact]
    public void The_winget_table_without_an_update_column_still_finds_the_source()
    {
        var lines = new[]
        {
            "Name                Id                  Version   Source",
            "----------------------------------------------------------",
            "Discord             Discord.Discord     1.0.9165  winget",
            "Steam App 12345     ARP\\Machine\\X64\\1  Unknown",
        };

        var found = PackageManagers.ParseWingetTable(lines);

        Assert.Single(found);
        Assert.Equal("Discord.Discord", found[0].Id);
        Assert.Equal("1.0.9165", found[0].Version);
    }

    [Fact]
    public void Matching_is_exact_on_the_winget_name_and_only_likely_elsewhere()
    {
        var programs = new List<InstalledProgram>
        {
            new("7-Zip 24.08 (x64)", "Igor Pavlov", "24.08", null, null, null, false),
            new("Notepad++ (64-bit x64)", "Notepad++ Team", "8.6", null, null, null, false),
            new("VLC media player", "VideoLAN", "3.0", null, null, null, false),
            new("A Very Long Program Name That Runs Off The Column", "P", "3.0", null, null, null, false),
            new("Nobody Knows This One", "Someone", "1.0", null, null, "https://example.test", false),
        };
        var managers = new ManagerReport(
            [new(Manager.Winget, "7zip.7zip", "7-Zip 24.08 (x64)", "24.08"), new(Manager.Winget, "Publisher.LongProgram", "A Very Long Program Name Tha\u2026", "3.0")], null,
            [new(Manager.Chocolatey, "notepadplusplus.install", "notepadplusplus.install", "8.6"), new(Manager.Chocolatey, "vlc", "vlc", "3.0")], null,
            [], "not installed", null);

        var matches = PackageManagers.Match(programs, managers);

        Assert.Equal(MatchConfidence.Exact, matches[0].Confidence);
        Assert.Equal("7zip.7zip", matches[0].Id);
        Assert.Equal(Manager.Chocolatey, matches[1].Manager);
        Assert.Equal("notepadplusplus.install", matches[1].Id);
        Assert.Equal(MatchConfidence.Likely, matches[1].Confidence);
        Assert.Equal("vlc", matches[2].Id);
        Assert.Equal("Publisher.LongProgram", matches[3].Id);
        Assert.Equal(MatchConfidence.Likely, matches[3].Confidence);
        Assert.True(matches[4].ByHand);
    }

    [Fact]
    public void Names_are_normalised_without_their_version_or_bitness()
    {
        Assert.Equal("7-Zip", InstalledProgram.Normalise("7-Zip 24.08 (x64 edition)"));
        Assert.Equal("Python", InstalledProgram.Normalise("Python 3.12.4 (64-bit)"));
        Assert.Equal("Notepad++", InstalledProgram.Normalise("Notepad++ (64-bit x64)"));
        Assert.Equal("notepadplusplus", PackageManagers.Slug("Notepad++"));
        Assert.Equal("7zip", PackageManagers.Slug("7-Zip"));
    }

    [Fact]
    public void The_lists_are_written_as_files_with_one_script_per_manager()
    {
        var programs = new List<InstalledProgram>
        {
            new("7-Zip", "Igor Pavlov", "24.08", new DateTime(2024, 8, 11), @"C:\Program Files\7-Zip", null, false),
            new("VLC media player", "VideoLAN", "3.0", null, null, null, false),
            new("By Hand Thing", "Someone", "1.0", null, null, "https://example.test", false),
        };
        var managers = new ManagerReport(
            [new(Manager.Winget, "7zip.7zip", "7-Zip", "24.08")], null,
            [new(Manager.Chocolatey, "vlc", "vlc", "3.0")], null,
            [], "not installed", null);
        var matches = PackageManagers.Match(programs, managers);

        var written = ProgramLists.Write(Path.Combine(_root, "lists"), matches, managers, new DateTime(2026, 9, 21, 10, 0, 0));

        var names = written.Files.Select(Path.GetFileName).ToList();
        Assert.Contains("programs.md", names);
        Assert.Contains("programs.json", names);
        Assert.Contains("winget-import.json", names);
        Assert.Contains("chocolatey-packages.config", names);
        Assert.Contains("install.ps1", names);
        var script = File.ReadAllText(Path.Combine(_root, "lists", "install.ps1"));
        Assert.Contains("winget import", script);
        Assert.Contains("choco install", script);
        Assert.Contains("By Hand Thing", script);
        Assert.Contains("https://example.test", script);
        Assert.Contains("\"PackageIdentifier\": \"7zip.7zip\"", File.ReadAllText(Path.Combine(_root, "lists", "winget-import.json")));
        Assert.Contains("<package id=\"vlc\" />", File.ReadAllText(Path.Combine(_root, "lists", "chocolatey-packages.config")));
    }

    [Fact]
    public void A_program_on_another_drive_keeps_its_files_and_is_listed_apart()
    {
        var onC = new InstalledProgram("On C", "P", "1", null, @"C:\Program Files\OnC", null, false);
        var onF = new InstalledProgram("On F", "P", "1", null, @"F:\Games\OnF", null, false);
        var unknown = new InstalledProgram("Says nothing", "P", "1", null, null, null, false);

        Assert.True(onC.OnWipedDrive(["C:"]));
        Assert.False(onF.OnWipedDrive(["C:"]));
        Assert.True(onF.OnWipedDrive(["C:", "F:"]));
        // No install location means the Windows drive, which is where per-user installs live.
        Assert.True(unknown.OnWipedDrive(["C:"]));

        var matches = PackageManagers.Match([onC, onF, unknown], ManagerReport.Empty);
        ProgramLists.Write(Path.Combine(_root, "drives"), matches, ManagerReport.Empty, new DateTime(2026, 9, 21), ["C:"]);

        var md = File.ReadAllText(Path.Combine(_root, "drives", "programs.md"));
        Assert.Contains("2 programs the wipe takes", md);
        Assert.Contains("## On a drive not being wiped (1)", md);
        var script = File.ReadAllText(Path.Combine(_root, "drives", "install.ps1"));
        Assert.Contains("By hand: 2 programs", script);
        Assert.Contains(@"On F  |  F:\Games\OnF", script);
    }

    [Fact]
    public void The_to_get_list_has_a_line_per_manager_and_a_search_hint_for_the_rest()
    {
        var programs = new List<InstalledProgram>
        {
            new("7-Zip", "Igor Pavlov", "24.08", null, null, null, false),
            new("VLC media player", "VideoLAN", "3.0", null, null, null, false),
            new("Discord", "Discord Inc.", "1.0", null, null, "https://discord.com", false),
            new("Barony", "Turning Wheel", "1", null, null, null, false, "Steam App 371970", "steam://uninstall/371970"),
        };
        var managers = new ManagerReport(
            [new(Manager.Winget, "7zip.7zip", "7-Zip", "24.08")], null,
            [new(Manager.Chocolatey, "vlc", "vlc", "3.0")], null,
            [], "not installed", null);
        var wanted = PackageManagers.Match(programs, managers);

        var files = ProgramLists.WriteToGet(Path.Combine(_root, "toget"), wanted, new DateTime(2026, 9, 21));

        var script = File.ReadAllText(files[1]);
        Assert.Contains("winget install --id 7zip.7zip", script);
        Assert.Contains("choco install vlc -y", script);
        Assert.Contains("Get-Command choco", script);
        Assert.Contains("# Discord (Discord Inc.): by hand.  https://discord.com", script);
        Assert.Contains("winget search --name \"Discord\"", script);
        Assert.Contains("# Barony: sign in to Steam", script);
        var md = File.ReadAllText(files[0]);
        Assert.Contains("4 programs ticked", md);
        Assert.Contains("| VLC media player | 3.0 | VideoLAN | choco install vlc |", md);
    }

    [Fact]
    public void The_library_folders_are_where_windows_says_and_are_grouped_first()
    {
        if (!OperatingSystem.IsWindows())
            return;
        var library = WipeList.LibraryFolders();
        Assert.Contains(library, l => l.Name == "Downloads");
        Assert.Contains(library, l => l.Name == "Documents");
        Assert.All(library, l => Assert.True(Path.IsPathRooted(l.Path)));

        var system = Path.GetPathRoot(Environment.GetFolderPath(Environment.SpecialFolder.Windows))!.TrimEnd('\\');
        var candidates = WipeList.Gather([system], k => k);
        var grouped = candidates.Where(c => c.Kind == FolderKind.Library).ToList();
        Assert.NotEmpty(grouped);
        // Not listed twice: a library folder is not also a profile row.
        Assert.DoesNotContain(candidates, c => c.Kind == FolderKind.Profile && grouped.Any(g => string.Equals(g.Path, c.Path, StringComparison.OrdinalIgnoreCase)));
        // Library comes first in the enum, so an ordered list puts the group at the top.
        Assert.True(FolderKind.Library < FolderKind.Profile);
    }

    [Fact]
    public void The_filter_narrows_the_lists_and_leaves_the_ticks_alone()
    {
        using var model = new RehomeViewModel(new FakeHost(Path.Combine(_root, "hostdata")));

        model.Filter = "nothing scanned yet";

        Assert.True(model.HasFilter);
        Assert.Empty(model.Programs);
        Assert.Empty(model.Folders);
        model.ClearFilterCommand.Execute(null);
        Assert.False(model.HasFilter);
        Assert.Equal(0, model.WantedCount);
    }

    [Fact]
    public void Cloud_only_files_count_only_when_the_option_says_to_pull_them_down()
    {
        var candidate = new WipeCandidate(@"C:\Users\x\OneDrive", FolderKind.Cloud, null);
        var row = new FolderRowViewModel(candidate, false) { Measured = new MeasuredFolder(candidate, Bytes: 100, Files: 2, Newest: null, Unreadable: 0, Skipped: 3, CloudBytes: 900) };

        Assert.Equal(100, row.Bytes);
        Assert.Equal(3, row.CloudOnly);
        Assert.Contains("skipped", row.DetailText);

        row.IncludeCloud = true;

        Assert.Equal(1000, row.Bytes);
        Assert.Contains("pull down", row.DetailText);
        Assert.True(row.IsLibrary);
        Assert.True(row.IsCloud);
    }

    [Fact]
    public void The_stored_path_is_the_source_path_with_the_drive_letter_as_a_folder()
    {
        Assert.Equal(@"C\Users\Dennis\AppData\Roaming\obs-studio", Manifest.StoredPathFor(@"C:\Users\Dennis\AppData\Roaming\obs-studio"));
        Assert.Equal(@"F\Games", Manifest.StoredPathFor(@"F:\Games"));
    }

    [Fact]
    public void The_copy_verifies_every_file_and_leaves_the_source_alone()
    {
        var source = Under("src");
        Under(@"src\a.txt", "hello");
        Under(@"src\deep\b.txt", "world");
        Under(@"src\deep\deeper\c.bin", new string('x', 3_000_000));
        var written = new DateTime(2024, 3, 4, 5, 6, 7);
        File.SetLastWriteTime(Path.Combine(source, "a.txt"), written);
        var destination = Path.Combine(_root, "dst");
        var progress = 0;

        var outcome = Carry.CopyTree(source, destination, _ => progress++, CancellationToken.None);

        Assert.Equal(3, outcome.Files);
        Assert.Equal(3, outcome.Verified);
        Assert.Empty(outcome.Failed);
        Assert.Equal(3, progress);
        Assert.Equal("world", File.ReadAllText(Path.Combine(destination, "deep", "b.txt")));
        Assert.Equal(written, File.GetLastWriteTime(Path.Combine(destination, "a.txt")));
        Assert.True(File.Exists(Path.Combine(source, "a.txt")));
        Assert.Equal(3, Directory.GetFiles(source, "*", SearchOption.AllDirectories).Length);
    }

    [Fact]
    public void Keep_mine_only_adds_what_is_missing()
    {
        var source = Under("keep-src");
        Under(@"keep-src\same.txt", "carried");
        Under(@"keep-src\new.txt", "carried");
        var destination = Under("keep-dst");
        Under(@"keep-dst\same.txt", "mine");

        var outcome = Carry.CopyTree(source, destination, null, CancellationToken.None, keepExisting: _ => true);

        Assert.Equal(1, outcome.Files);
        Assert.Equal(1, outcome.Skipped);
        Assert.Equal("mine", File.ReadAllText(Path.Combine(destination, "same.txt")));
        Assert.Equal("carried", File.ReadAllText(Path.Combine(destination, "new.txt")));
    }

    [Fact]
    public void The_manifest_round_trips_and_names_a_folder_for_today()
    {
        var root = Carry.RootFor(_root, new DateTime(2026, 9, 21));
        Directory.CreateDirectory(root);
        var manifest = new Manifest
        {
            CreatedAt = new DateTime(2026, 9, 21, 10, 0, 0),
            Machine = "BOX",
            User = "dennis",
            WipedDrives = ["C:"],
            Entries = [new ManifestEntry { Source = @"C:\Users\dennis\Documents", Stored = @"C\Users\dennis\Documents", Kind = "Profile", Files = 2, Bytes = 10, Verified = 2 }],
            Extras = [new ManifestExtra { Kind = "Hosts", Stored = @"extras\hosts", Ok = true }],
        };

        manifest.Save(root);
        var back = Manifest.Load(root);

        Assert.EndsWith("Rehome 2026-09-21", root);
        Assert.NotNull(back);
        Assert.Equal("BOX", back!.Machine);
        Assert.Single(back.Entries);
        Assert.True(back.Entries[0].Complete);
        Assert.Equal(@"C:\Users\dennis\Documents", back.Entries[0].Source);
        Assert.Single(back.Extras);
        // A second pack on the same day gets its own folder rather than merging into the first.
        Assert.EndsWith("Rehome 2026-09-21 (2)", Carry.RootFor(_root, new DateTime(2026, 9, 21)));
    }

    [Fact]
    public void Measuring_a_folder_counts_what_the_copy_would_carry()
    {
        Under(@"measure\one.txt", "12345");
        Under(@"measure\sub\two.txt", "123");
        var candidate = new WipeCandidate(Path.Combine(_root, "measure"), FolderKind.Profile, null);

        var measured = WipeList.Measure(candidate, CancellationToken.None);

        Assert.Equal(2, measured.Files);
        Assert.Equal(8, measured.Bytes);
        Assert.NotNull(measured.Newest);
        Assert.Equal(0, measured.Skipped);
    }

    [Fact]
    public void The_way_back_reads_the_manifest_and_ticks_what_is_missing_here()
    {
        var root = Under("back");
        var source = Path.Combine(_root, "back-home", "Documents");
        var stored = Manifest.StoredPathFor(source);
        Under(Path.Combine("back", stored, "note.txt"), "carried");
        new Manifest
        {
            CreatedAt = DateTime.Now,
            Machine = "BOX",
            User = "dennis",
            Entries = [new ManifestEntry { Source = source, Stored = stored, Kind = "Profile", Files = 1, Bytes = 7, Verified = 1 }],
        }.Save(root);
        using var model = new RehomeViewModel(new FakeHost(Path.Combine(_root, "hostdata")));

        model.LoadRehomeFolder(root);

        Assert.True(model.HasManifest);
        Assert.Single(model.RestoreRows);
        Assert.True(model.RestoreRows[0].HasCopy);
        Assert.False(model.RestoreRows[0].Exists);
        Assert.True(model.RestoreRows[0].IsPicked);
        Assert.True(model.BringBackCommand.CanExecute(null));
        Assert.Equal(RehomeStep.WayBack, model.SelectedStep!.Step);
    }

    [Fact]
    public void A_key_decodes_to_five_blocks_or_to_nothing()
    {
        // Too short to hold a key is honestly nothing, not a string of B's.
        Assert.Null(Keys.Decode(new byte[10]));
        Assert.Null(Keys.Decode(new byte[80]));

        if (!OperatingSystem.IsWindows())
            return;
        var key = Keys.RegistryKey();
        if (key is null)
            return;
        Assert.Equal(29, key.Length);
        Assert.Equal(4, key.Count(c => c == '-'));
        Assert.All(key.Split('-'), block => Assert.Equal(5, block.Length));
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
