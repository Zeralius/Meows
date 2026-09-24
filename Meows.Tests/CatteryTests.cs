using System.Diagnostics;
using Meows.Plugins.Cattery.Services;
using Meows.Plugins.Cattery.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Cattery: repositories found without walking into them, git's own porcelain read for the
/// branch, the work and ahead and behind, the most neglected first, and the last answer kept.
/// </summary>
public sealed class CatteryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "cattery-" + Guid.NewGuid().ToString("N")[..10]);

    public CatteryTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            foreach (var file in Directory.EnumerateFiles(_root, "*", SearchOption.AllDirectories))
                File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private static void Git(string folder, params string[] arguments)
    {
        var start = new ProcessStartInfo("git") { WorkingDirectory = folder, RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var a in (string[])["-c", "user.name=t", "-c", "user.email=t@t", "-c", "init.defaultBranch=main", .. arguments])
            start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        process.WaitForExit();
    }

    private string Repo(string relative)
    {
        var folder = Path.Combine(_root, relative);
        Directory.CreateDirectory(folder);
        Git(folder, "init", "-q");
        File.WriteAllText(Path.Combine(folder, "readme.md"), "hi");
        Git(folder, "add", ".");
        Git(folder, "commit", "-qm", "first");
        return folder;
    }

    [Fact]
    public void Porcelain_is_read_for_branch_work_and_distance()
    {
        var state = Repos.Parse("/x/bot", string.Join('\n',
            "# branch.oid abc",
            "# branch.head main",
            "# branch.upstream origin/main",
            "# branch.ab +3 -1",
            "1 .M N... 100644 100644 100644 a b file.cs",
            "2 R. N... 100644 100644 100644 a b R100 new.cs\told.cs",
            "u UU N... 100644 100644 100644 100644 a b c conflict.cs",
            "? scratch.txt",
            "? more.txt"));

        Assert.Equal("main", state.Branch);
        Assert.Equal(2, state.Changed);
        Assert.Equal(1, state.Conflicted);
        Assert.Equal(2, state.Untracked);
        Assert.Equal((3, 1), (state.Ahead, state.Behind));
        Assert.True(state.IsDirty);
        Assert.True(state.HasUnpushed);
        Assert.Equal("bot", state.Name);

        var detached = Repos.Parse("/x/y", "# branch.oid abc\n# branch.head (detached)\n");
        Assert.True(detached.IsDetached);
        Assert.False(detached.IsDirty);
    }

    [Fact]
    public void Repositories_are_found_without_walking_into_them_or_into_build_output()
    {
        var bot = Repo(Path.Combine("Programmierung", "bot"));
        Repo(Path.Combine("Programmierung", "bot", "vendored"));
        Repo(Path.Combine("Programmierung", "web", "node_modules", "left-pad"));
        var deep = Repo(Path.Combine("Programmierung", "games", "unity", "Kat3DWorks"));

        var found = Repos.Find(Path.Combine(_root, "Programmierung"));

        Assert.Equal([bot, deep], found);
    }

    [Fact]
    public void Git_is_asked_and_the_most_neglected_comes_first()
    {
        var git = Repos.GitExecutable();
        if (git is null)
            return;

        var clean = Repo(Path.Combine("p", "clean"));
        var dirty = Repo(Path.Combine("p", "dirty"));
        File.WriteAllText(Path.Combine(dirty, "readme.md"), "changed");
        File.WriteAllText(Path.Combine(dirty, "new.txt"), "new");

        var host = new FakeHost(Path.Combine(_root, "host"));
        using (var model = new CatteryViewModel(host, () => null))
        {
            model.AddRoot(Path.Combine(_root, "p"));
            Assert.True(model.HasError);

            model.Show(CatteryViewModel.Read(git, model.Roots, CancellationToken.None));

            Assert.Equal(["dirty", "clean"], model.Repositories.Select(r => r.Name));
            var first = model.Repositories[0];
            Assert.Equal("main", first.BranchText);
            Assert.Equal("1 changed, 1 new", first.WorkText);
            Assert.Equal("no upstream", first.RemoteText);
            Assert.Equal("clean", model.Repositories[1].WorkText);
            Assert.Contains("1 with uncommitted work", model.Summary);
            Assert.Contains("dirty", model.Glance()!.Text);

            model.FilterIndex = 1;
            Assert.Equal(["dirty"], model.Repositories.Select(r => r.Name));
        }

        // The last answer is there the moment the tab opens again.
        using var again = new CatteryViewModel(host, () => null);
        Assert.Equal(["dirty"], again.Repositories.Select(r => r.Name));
        Assert.Equal([clean, dirty], again.Roots.SelectMany(r => Repos.Find(r)).Order(StringComparer.Ordinal));
    }
}
