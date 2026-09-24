using Meows.Plugins.Nest.Services;
using Meows.Plugins.Nest.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Nest: what each place holds, how its copy stands, a copy that only ever adds and checks what
/// it wrote, and places that are a starting point the person can switch off and add to.
/// </summary>
public sealed class NestTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "nest-" + Guid.NewGuid().ToString("N")[..10]);

    public NestTests() => Directory.CreateDirectory(_root);

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

    private string Place(string name, params (string File, string Text)[] files)
    {
        var folder = Path.Combine(_root, "home", name);
        foreach (var (file, text) in files)
        {
            var path = Path.Combine(folder, file);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, text);
        }
        Directory.CreateDirectory(folder);
        return folder;
    }

    [Fact]
    public void A_copy_only_adds_and_says_what_changed_since()
    {
        var saves = Place("Saved Games", ("Game/slot1.sav", "one"), ("Game/slot2.sav", "two"));
        var copy = Path.Combine(_root, "stick", "saved-games");

        Assert.False(NestPlaces.Standing(saves, copy).HasCopy);
        Assert.Equal(2, NestPlaces.Standing(saves, copy).Changed);

        var first = NestPlaces.CopyChanged(saves, copy, null, CancellationToken.None);
        Assert.Equal(2, first.Copied);
        Assert.True(NestPlaces.Standing(saves, copy).UpToDate);
        Assert.Empty(Directory.GetFiles(copy, "*.meows-part", SearchOption.AllDirectories));

        // Changed here, deleted here: the change is copied, the deleted one stays in the copy.
        File.WriteAllText(Path.Combine(saves, "Game", "slot1.sav"), "one, further along");
        File.SetLastWriteTimeUtc(Path.Combine(saves, "Game", "slot1.sav"), DateTime.UtcNow.AddMinutes(1));
        File.Delete(Path.Combine(saves, "Game", "slot2.sav"));
        var standing = NestPlaces.Standing(saves, copy);
        Assert.Equal(1, standing.Changed);

        var second = NestPlaces.CopyChanged(saves, copy, null, CancellationToken.None);
        Assert.Equal(1, second.Copied);
        Assert.Equal("one, further along", File.ReadAllText(Path.Combine(copy, "Game", "slot1.sav")));
        Assert.Equal("two", File.ReadAllText(Path.Combine(copy, "Game", "slot2.sav")));
    }

    [Fact]
    public void A_folder_added_by_hand_gets_a_copy_of_its_own()
    {
        var a = NestPlaces.Added(Path.Combine(_root, "one", "Saves"));
        var b = NestPlaces.Added(Path.Combine(_root, "two", "Saves"));

        Assert.StartsWith("Saves-", a.Key);
        Assert.NotEqual(a.Key, b.Key);
        Assert.Equal(a.Key, NestPlaces.Added(Path.Combine(_root, "one", "Saves") + Path.DirectorySeparatorChar).Key);
    }

    [Fact]
    public void The_tab_counts_what_matters_copies_it_and_remembers_what_is_off()
    {
        var saves = Place("Saved Games", ("a.sav", "12345"));
        var keys = Place("ssh", ("id_ed25519", "secret"));
        var missing = Path.Combine(_root, "home", "not-here");
        NestPlace[] known =
        [
            new("saved-games", "nest.place.savedgames", saves, true),
            new("ssh", "nest.place.ssh", keys, true),
            new("minecraft", "nest.place.minecraft", missing, true),
        ];
        var host = new FakeHost(Path.Combine(_root, "host"));

        using (var model = new NestViewModel(host, () => known))
        {
            // A known place that is not on this machine is not shown at all.
            Assert.Equal(["Saved Games", "SSH keys"], model.Places.Select(p => p.Name));
            model.Measured(NestViewModel.MeasureAll(model.Places.Select(p => p.Place).ToList(), null, CancellationToken.None));
            Assert.Contains("11 B in 2 files", model.Total);
            Assert.Contains("never copied", model.Glance()!.Text);
            Assert.False(model.Glance()!.IsTrouble);

            model.Places.Single(p => p.Place.Key == "ssh").IsOn = false;
            Assert.Contains("5 B in 1 files", model.Total);

            var stick = Path.Combine(_root, "stick");
            model.SetCopyRoot(stick);
            var on = model.Places.Where(p => p.IsOn).Select(p => p.Place).ToList();
            model.Copied(on, NestViewModel.CopyAll(on, stick, null, CancellationToken.None));
            Assert.True(File.Exists(Path.Combine(stick, "saved-games", "a.sav")));
            Assert.False(Directory.Exists(Path.Combine(stick, "ssh")));
            Assert.Contains(host.Store.Events, e => e.Kind == "copied");

            model.SetCopyRoot(Path.Combine(saves, "backup"));
            Assert.True(model.HasError);
            Assert.Equal(stick, model.CopyRoot);
        }

        using var again = new NestViewModel(host, () => known);
        Assert.False(again.Places.Single(p => p.Place.Key == "ssh").IsOn);
        Assert.NotNull(again.Places.Single(p => p.Place.Key == "saved-games").LastCopyUtc);
    }
}
