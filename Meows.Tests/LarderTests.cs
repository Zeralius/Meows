using Meows.Disk;
using Meows.Plugins.Larder.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Larder: every game read from Steam's own manifests, never played kept apart from no record,
/// the never-touched largest first, and nothing it does touches a game folder.
/// </summary>
public sealed class LarderTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "larder-" + Guid.NewGuid().ToString("N")[..10]);

    public LarderTests() => Directory.CreateDirectory(_root);

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

    private static readonly long Played = (long)(DateTime.UtcNow.AddYears(-2) - DateTime.UnixEpoch).TotalSeconds;
    private static readonly long Recent = (long)(DateTime.UtcNow.AddDays(-3) - DateTime.UnixEpoch).TotalSeconds;

    /// <summary>A library with manifests only; Larder never needs the games themselves.</summary>
    private string Library(string name, params (string Id, string Name, long Size, long? LastPlayed)[] games)
    {
        var library = Path.Combine(_root, name);
        var steamapps = Path.Combine(library, "steamapps");
        Directory.CreateDirectory(steamapps);
        foreach (var (id, game, size, played) in games)
        {
            var lines = new List<string>
            {
                "\"AppState\"", "{",
                $"\t\"appid\"\t\t\"{id}\"",
                $"\t\"name\"\t\t\"{game}\"",
                $"\t\"installdir\"\t\t\"{game.Replace(' ', '_')}\"",
                $"\t\"SizeOnDisk\"\t\t\"{size}\"",
                "\t\"LastUpdated\"\t\t\"1700000000\"",
            };
            if (played is { } seconds)
                lines.Add($"\t\"LastPlayed\"\t\t\"{seconds}\"");
            lines.Add("}");
            File.WriteAllLines(Path.Combine(steamapps, $"appmanifest_{id}.acf"), lines);
        }
        File.WriteAllText(Path.Combine(steamapps, "appmanifest_999.acf"), "not a manifest at all");
        return library;
    }

    [Fact]
    public void Manifests_are_read_with_never_played_and_no_record_kept_apart()
    {
        var library = Library("F", ("10", "Big Unplayed", 90_000, 0), ("20", "Mystery", 50_000, null), ("30", "Old Favourite", 10_000, Played));

        var games = SteamLibrary.InstalledIn(library);

        // The file that is not a manifest is no game, and costs nothing else.
        Assert.Equal(3, games.Count);
        var big = games.Single(g => g.AppId == "10");
        Assert.True(big.NeverPlayed);
        Assert.False(big.PlayedUnknown);
        Assert.Equal(Path.Combine(library, "steamapps", "common", "Big_Unplayed"), big.InstallFolder);
        Assert.True(games.Single(g => g.AppId == "20").PlayedUnknown);
        Assert.False(games.Single(g => g.AppId == "20").NeverPlayed);
        Assert.NotNull(big.LastUpdated);
        Assert.Empty(SteamLibrary.InstalledIn(Path.Combine(_root, "not-there")));
    }

    [Fact]
    public void The_list_puts_the_never_touched_largest_first_and_counts_what_they_hold()
    {
        var f = Library("F", ("1", "Small Never", 1_000, 0), ("2", "Huge Never", 9_000_000, 0), ("3", "Recent", 5_000, Recent));
        var g = Library("G", ("4", "Mystery", 7_000, null), ("5", "Old", 3_000, Played), ("1", "Small Never", 1_000, 0));
        using var model = new LarderViewModel(new FakeHost(Path.Combine(_root, "host")), () => [f, g]);

        var libraries = model.AllLibraries();
        model.Show(LarderViewModel.Read(libraries), libraries);

        // One game in two libraries is counted once.
        Assert.Equal(["Huge Never", "Small Never", "Mystery", "Old", "Recent"], model.Games.Select(v => v.Name));
        Assert.Contains("2 never launched", model.NeverLine);
        Assert.Contains("1 with no record", model.UnknownLine);
        Assert.Equal("never launched", model.Games[0].PlayedText);
        Assert.Equal("no record", model.Games[2].PlayedText);
        Assert.Contains("2 games never launched", model.Glance()!.Text);
        Assert.False(model.Glance()!.IsTrouble);

        model.FilterIndex = 2;
        Assert.Equal(["Old"], model.Games.Select(v => v.Name));
        model.FilterIndex = 3;
        Assert.Equal(["Mystery"], model.Games.Select(v => v.Name));
        model.FilterIndex = 0;
        model.SortIndex = 1;
        Assert.Equal("Huge Never", model.Games[0].Name);
        Assert.Equal(2, model.Libraries.Count);
        Assert.Equal(3, model.Libraries[0].Games);
    }

    [Fact]
    public void A_library_added_by_hand_is_remembered_and_a_steamapps_pick_means_its_library()
    {
        var host = new FakeHost(Path.Combine(_root, "host"));
        var library = Library("H", ("7", "Game", 100, 0));
        using (var model = new LarderViewModel(host, () => []))
        {
            model.AddLibrary(Path.Combine(library, "steamapps"));
            Assert.Equal([library], model.ExtraLibraries);
            model.AddLibrary(library);
            Assert.Single(model.ExtraLibraries);
        }

        using var again = new LarderViewModel(host, () => []);
        Assert.Equal([library], again.AllLibraries());
    }

    [Fact]
    public void How_long_ago_reads_in_one_natural_unit()
    {
        var now = new DateTime(2026, 9, 24, 12, 0, 0);
        Assert.Equal("today", LarderViewModel.Ago(now.AddHours(-3), now));
        Assert.Equal("5 days ago", LarderViewModel.Ago(now.AddDays(-5), now));
        Assert.Equal("4 months ago", LarderViewModel.Ago(now.AddDays(-125), now));
        Assert.Equal("3 years ago", LarderViewModel.Ago(now.AddYears(-3), now));
    }
}
