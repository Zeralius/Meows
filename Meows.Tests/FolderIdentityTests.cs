using Meows.Disk;

namespace Meows.Tests;

public sealed class SteamLibraryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "steam-" + Guid.NewGuid().ToString("N")[..10]);

    public SteamLibraryTests() => Directory.CreateDirectory(_root);

    /// <summary>
    /// Builds the layout Steam uses: the game under steamapps\common with the manifest naming
    /// it two levels up. Matching that pair is what the code has to do.
    /// </summary>
    private string Install(string installDir, string name, long size, long? lastPlayed)
    {
        var steamapps = Path.Combine(_root, "steamapps");
        var game = Path.Combine(steamapps, "common", installDir);
        Directory.CreateDirectory(game);

        var played = lastPlayed is null ? "" : $"\t\"LastPlayed\"\t\t\"{lastPlayed}\"\n";
        File.WriteAllText(Path.Combine(steamapps, $"appmanifest_{Math.Abs(installDir.GetHashCode())}.acf"),
            "\"AppState\"\n{\n" +
            $"\t\"name\"\t\t\"{name}\"\n" +
            $"\t\"installdir\"\t\t\"{installDir}\"\n" +
            $"\t\"SizeOnDisk\"\t\t\"{size}\"\n" +
            played +
            "}\n");

        return game;
    }

    [Fact]
    public void A_game_folder_is_matched_to_its_manifest()
    {
        var game = Install("CrownTrick", "Crown Trick", 1_753_781_097, lastPlayed: 0);

        var found = SteamLibrary.GameAt(game);

        Assert.NotNull(found);
        Assert.Equal("Crown Trick", found!.Name);
        Assert.Equal(1_753_781_097, found.SizeOnDisk);
    }

    [Fact]
    public void The_name_comes_from_the_manifest_rather_than_the_folder()
    {
        // The folder is called WRATH and the game is called WRATH: Aeon of Ruin. Steam knows.
        var game = Install("WRATH", "WRATH: Aeon of Ruin", 1_600_000_000, lastPlayed: 1_750_000_000);

        Assert.Equal("WRATH: Aeon of Ruin", SteamLibrary.GameAt(game)!.Name);
    }

    [Fact]
    public void Never_played_and_not_recorded_are_different_answers()
    {
        var never = Install("NeverTouched", "Never Touched", 100, lastPlayed: 0);
        var unknown = Install("NoRecord", "No Record", 100, lastPlayed: null);
        var played = Install("Played", "Played", 100, lastPlayed: 1_750_000_000);

        Assert.True(SteamLibrary.GameAt(never)!.NeverPlayed);
        Assert.False(SteamLibrary.GameAt(never)!.PlayedUnknown);

        // Steam leaving the key out is not the same as Steam saying never, and folding the two
        // together would overstate how much has gone unplayed.
        Assert.True(SteamLibrary.GameAt(unknown)!.PlayedUnknown);
        Assert.False(SteamLibrary.GameAt(unknown)!.NeverPlayed);

        Assert.False(SteamLibrary.GameAt(played)!.NeverPlayed);
        Assert.False(SteamLibrary.GameAt(played)!.PlayedUnknown);
    }

    [Fact]
    public void A_folder_that_is_not_a_steam_game_is_not_claimed_as_one()
    {
        var ordinary = Path.Combine(_root, "just a folder");
        Directory.CreateDirectory(ordinary);

        Assert.Null(SteamLibrary.GameAt(ordinary));
        Assert.Null(SteamLibrary.GameAt(Path.Combine(_root, "does not exist")));
    }

    [Fact]
    public void A_folder_under_common_with_no_manifest_naming_it_is_not_a_game()
    {
        Install("RealGame", "Real Game", 100, lastPlayed: 0);

        // Left behind after an uninstall, with nothing in steamapps pointing at it.
        var orphan = Path.Combine(_root, "steamapps", "common", "LeftBehind");
        Directory.CreateDirectory(orphan);

        Assert.Null(SteamLibrary.GameAt(orphan));
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

public sealed class FolderInspectorTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sniff-" + Guid.NewGuid().ToString("N")[..10]);

    public FolderInspectorTests() => Directory.CreateDirectory(_root);

    private string Folder(string name, int files = 3, string extension = ".dat")
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(path);
        for (var i = 0; i < files; i++)
            File.WriteAllBytes(Path.Combine(path, $"file{i}{extension}"), new byte[16]);
        return path;
    }

    [Fact]
    public void A_build_folder_is_named_and_called_rebuildable()
    {
        var identity = FolderInspector.Of(Folder("obj"));

        Assert.Equal(FolderVerdict.Rebuildable, identity.Verdict);
        Assert.Contains("build output", identity.Headline);
        Assert.Contains("Safe to remove", identity.Advice);
    }

    [Fact]
    public void A_folder_nothing_can_be_established_about_says_so()
    {
        var identity = FolderInspector.Of(Folder("wehjfkwe", extension: ".qqq"));

        // Refusing to guess is the point. A confident wrong answer here deletes something.
        Assert.Equal(FolderVerdict.Unknown, identity.Verdict);
        Assert.Contains("treat it as yours", identity.Advice);
    }

    [Fact]
    public void Scratch_space_is_not_mistaken_for_an_applications_data()
    {
        // Temp sits under AppData\Local, so treating the first folder under Local as an
        // application name confidently reports every scratch folder as belonging to a program
        // called Temp. These test folders are themselves under Temp, which is how it surfaced.
        var identity = FolderInspector.Of(Folder("scratch", extension: ".qqq"));

        Assert.NotEqual(FolderVerdict.ApplicationData, identity.Verdict);
        Assert.DoesNotContain(identity.Evidence, e => e.Contains("Temp's own folder"));
    }

    [Fact]
    public void A_folder_that_is_not_there_is_not_described()
    {
        var identity = FolderInspector.Of(Path.Combine(_root, "no such folder"));

        Assert.Equal(FolderVerdict.Unknown, identity.Verdict);
        Assert.Empty(identity.Evidence);
    }

    [Fact]
    public void The_verdict_always_comes_with_the_reasons_for_it()
    {
        var identity = FolderInspector.Of(Folder("node_modules"));

        // The reasoning is the part that lets someone disagree with it.
        Assert.NotEmpty(identity.Evidence);
        Assert.Contains(identity.Evidence, e => e.Contains("npm"));
    }

    [Fact]
    public void Your_own_documents_folder_is_never_offered_up()
    {
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        if (!Directory.Exists(documents))
            return;

        var identity = FolderInspector.Of(documents);

        Assert.Equal(FolderVerdict.Yours, identity.Verdict);
        Assert.Contains("your own", identity.Advice);
    }

    [Fact]
    public void A_steam_game_is_answered_for_by_steam_rather_than_by_the_files()
    {
        var steamapps = Path.Combine(_root, "steamapps");
        var game = Path.Combine(steamapps, "common", "SomeGame");
        Directory.CreateDirectory(game);
        File.WriteAllBytes(Path.Combine(game, "game.exe"), new byte[16]);
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_1.acf"),
            "\"AppState\"\n{\n\t\"name\"\t\t\"Some Game\"\n\t\"installdir\"\t\t\"SomeGame\"\n" +
            "\t\"SizeOnDisk\"\t\t\"5000\"\n\t\"LastPlayed\"\t\t\"0\"\n}\n");

        var identity = FolderInspector.Of(game);

        Assert.Equal(FolderVerdict.Game, identity.Verdict);
        Assert.Contains("Some Game", identity.Headline);
        // The one case where the right answer is emphatically not "delete it yourself".
        Assert.Contains("through Steam", identity.Advice);
        Assert.Contains(identity.Evidence, e => e.Contains("never been launched"));
    }

    [Fact]
    public void A_file_held_open_is_noticed()
    {
        var folder = Folder("busy", files: 1);
        using var held = File.Open(Path.Combine(folder, "file0.dat"),
            FileMode.Open, FileAccess.Read, FileShare.None);

        var identity = FolderInspector.Of(folder);

        Assert.True(identity.InUse);
        Assert.Contains(identity.Evidence, e => e.Contains("open right now"));
    }

    [Fact]
    public void A_folder_nothing_is_holding_is_not_reported_as_busy()
    {
        Assert.False(FolderInspector.Of(Folder("quiet")).InUse);
    }

    [Fact]
    public void An_empty_folder_says_it_is_empty_rather_than_guessing()
    {
        var empty = Path.Combine(_root, "hollow");
        Directory.CreateDirectory(empty);

        var identity = FolderInspector.Of(empty);

        Assert.Contains(identity.Evidence, e => e.Contains("No files inside"));
    }

    [Fact]
    public void Cancelling_does_not_throw()
    {
        var folder = Folder("plenty", files: 20);
        using var source = new CancellationTokenSource();
        source.Cancel();

        var identity = FolderInspector.Of(folder, source.Token);

        Assert.NotNull(identity);
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
