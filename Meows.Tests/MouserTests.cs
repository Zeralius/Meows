using System.Text;
using Meows.Plugins.Mouser.Services;

namespace Meows.Tests;

public sealed class ShellLinkTests
{
    /// <summary>
    /// A shortcut with nothing in it but a LinkInfo block, which is the part that carries the
    /// path. Built by hand so the reader is tested against the format rather than against
    /// whatever happens to be installed on the machine running the tests.
    /// </summary>
    private static byte[] Link(string basePath, string suffix = "", bool unicode = false)
    {
        var headerSize = unicode ? 0x24 : 0x1C;
        var body = new List<byte>();

        var baseAt = headerSize;
        body.AddRange(Encoding.Latin1.GetBytes(basePath));
        body.Add(0);

        var suffixAt = headerSize + body.Count;
        body.AddRange(Encoding.Latin1.GetBytes(suffix));
        body.Add(0);

        var baseWideAt = 0;
        var suffixWideAt = 0;
        if (unicode)
        {
            baseWideAt = headerSize + body.Count;
            body.AddRange(Encoding.Unicode.GetBytes(basePath));
            body.AddRange([0, 0]);

            suffixWideAt = headerSize + body.Count;
            body.AddRange(Encoding.Unicode.GetBytes(suffix));
            body.AddRange([0, 0]);
        }

        var info = new byte[headerSize + body.Count];
        BitConverter.GetBytes(info.Length).CopyTo(info, 0);
        BitConverter.GetBytes(headerSize).CopyTo(info, 4);
        BitConverter.GetBytes(1).CopyTo(info, 8);   // VolumeIdAndLocalBasePath
        BitConverter.GetBytes(baseAt).CopyTo(info, 16);
        BitConverter.GetBytes(suffixAt).CopyTo(info, 24);
        if (unicode)
        {
            BitConverter.GetBytes(baseWideAt).CopyTo(info, 28);
            BitConverter.GetBytes(suffixWideAt).CopyTo(info, 32);
        }

        body.CopyTo(info, headerSize);

        var bytes = new byte[0x4C + info.Length];
        BitConverter.GetBytes(0x4C).CopyTo(bytes, 0);
        BitConverter.GetBytes(2u).CopyTo(bytes, 20);  // HasLinkInfo, no target id list
        info.CopyTo(bytes, 0x4C);
        return bytes;
    }

    [Fact]
    public void The_path_a_shortcut_points_at_is_read_out_of_it()
    {
        Assert.Equal(@"C:\Games\thing.exe", ShellLink.TargetIn(Link(@"C:\Games\thing.exe")));
    }

    [Fact]
    public void The_two_halves_of_a_path_are_joined_back_together()
    {
        // Windows splits the path at the point the volume stops being involved.
        Assert.Equal(@"C:\Games\thing.exe", ShellLink.TargetIn(Link(@"C:\Games\", "thing.exe")));
    }

    [Fact]
    public void The_wide_copy_wins_when_the_shortcut_carries_one()
    {
        var target = ShellLink.TargetIn(Link(@"C:\Jeux\Pokémon.exe", unicode: true));

        Assert.Equal(@"C:\Jeux\Pokémon.exe", target);
    }

    [Fact]
    public void An_accented_old_style_path_survives_being_read()
    {
        // Encoding.Default is UTF-8 on .NET, which turns every accented byte in one of these
        // into a replacement character and makes a working shortcut look dead.
        var target = ShellLink.TargetIn(Link(@"C:\Jeux\Pokémon.exe"));

        Assert.Equal(@"C:\Jeux\Pokémon.exe", target);
        Assert.DoesNotContain('\uFFFD', target!);
    }

    [Fact]
    public void Something_that_is_not_a_shortcut_reads_as_nothing_rather_than_throwing()
    {
        Assert.Null(ShellLink.TargetIn([]));
        Assert.Null(ShellLink.TargetIn(new byte[0x4C]));
        Assert.Null(ShellLink.TargetIn(Encoding.UTF8.GetBytes("this is a text file")));
    }

    [Fact]
    public void A_network_shortcut_is_not_answered_for()
    {
        var bytes = Link(@"C:\somewhere\thing.exe");
        // Clear VolumeIdAndLocalBasePath, which is what a share only shortcut looks like.
        BitConverter.GetBytes(0).CopyTo(bytes, 0x4C + 8);

        Assert.Null(ShellLink.TargetIn(bytes));
    }

    [Fact]
    public void A_truncated_shortcut_does_not_read_off_the_end()
    {
        var full = Link(@"C:\Games\thing.exe");

        for (var length = 0x4C; length < full.Length; length++)
        {
            var cut = full[..length];
            var caught = Record.Exception(() => ShellLink.TargetIn(cut));
            Assert.Null(caught);
        }
    }

    [Fact]
    public void A_missing_file_reads_as_nothing()
    {
        Assert.Null(ShellLink.TargetOf(Path.Combine(Path.GetTempPath(), "no-such-" + Guid.NewGuid().ToString("N") + ".lnk")));
    }
}

public sealed class MouserScanTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "mouser-" + Guid.NewGuid().ToString("N")[..10]);

    public MouserScanTests() => Directory.CreateDirectory(_root);

    private string Folder(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(path);
        return path;
    }

    private string File(string name, int bytes = 100)
    {
        var path = Path.Combine(_root, name);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        System.IO.File.WriteAllBytes(path, new byte[bytes]);
        return path;
    }

    private IReadOnlyList<Finding> Run(bool skipSystemFolders = true) =>
        MouserScan.Run(_root, new MouserOptions { SkipSystemFolders = skipSystemFolders }, null, CancellationToken.None)
            .Findings;

    [Fact]
    public void An_empty_folder_is_found()
    {
        Folder("nothing here");

        var found = Run();

        var folder = Assert.Single(found);
        Assert.Equal(DeadKind.EmptyFolder, folder.Kind);
        Assert.Equal("nothing here", folder.Name);
    }

    [Fact]
    public void A_folder_holding_only_empty_folders_counts_as_empty_too()
    {
        Folder("outer", "middle", "inner");

        var found = Run();

        // One finding, the outermost. Removing it takes the rest, so listing all three would be
        // noise and two of the deletes would be of something already gone.
        var folder = Assert.Single(found);
        Assert.Equal(Path.Combine(_root, "outer"), folder.Path);
    }

    [Fact]
    public void A_folder_with_a_file_somewhere_below_is_left_alone()
    {
        File(Path.Combine("outer", "middle", "kept.txt"));

        Assert.Empty(Run());
    }

    [Fact]
    public void The_folder_you_pointed_at_is_never_offered()
    {
        // The root is empty here, and offering to delete what was just chosen would be absurd.
        Assert.Empty(Run());
    }

    [Fact]
    public void A_zero_byte_file_is_found_and_a_real_one_is_not()
    {
        File("hollow.txt", 0);
        File("real.txt", 50);

        var found = Run();

        var empty = Assert.Single(found);
        Assert.Equal(DeadKind.EmptyFile, empty.Kind);
        Assert.Equal("hollow.txt", empty.Name);
    }

    [Fact]
    public void A_file_whose_whole_job_is_to_be_empty_is_left_alone()
    {
        // Every one of these is doing exactly what it is for. An empty __init__.py is what makes
        // a Python package a package, and a .gitkeep exists only so git carries the folder.
        File(Path.Combine("pkg", "__init__.py"), 0);
        File(Path.Combine("pkg", "py.typed"), 0);
        File(Path.Combine("empties", ".gitkeep"), 0);
        File(Path.Combine("empties", ".keep"), 0);

        Assert.DoesNotContain(Run(), f => f.Kind == DeadKind.EmptyFile);
    }

    [Fact]
    public void Marker_files_a_build_tool_scatters_about_are_left_alone()
    {
        // Unity writes thousands of these into a project. The file existing is the whole message,
        // so its size says nothing about whether anything wants it.
        File(Path.Combine("Library", "thing.ref.dll_ABCDEF.mvfrm"), 0);
        File(Path.Combine("Library", "package.ModuleCompilationTrigger"), 0);
        File(Path.Combine("Library", "held.lock"), 0);

        Assert.DoesNotContain(Run(), f => f.Kind == DeadKind.EmptyFile);
    }

    [Fact]
    public void A_folder_holding_only_files_that_are_meant_to_be_empty_is_not_called_empty()
    {
        File(Path.Combine("pkg", "__init__.py"), 0);

        // Nothing there is on offer, so nothing about the folder has changed and calling it empty
        // would invite deleting a package that works perfectly well.
        Assert.Empty(Run());
    }

    [Fact]
    public void An_empty_file_with_no_extension_is_a_note_a_program_left_itself()
    {
        File(Path.Combine("app", "REQUESTED"), 0);
        File(Path.Combine("app", "WEBGL_SUPPORTED"), 0);
        File(Path.Combine("app", "CodeSignature"), 0);

        // Nothing has ever opened these, and reclaiming zero bytes is not worth the chance of
        // breaking whatever wrote them.
        Assert.DoesNotContain(Run(), f => f.Kind == DeadKind.EmptyFile);
    }

    [Fact]
    public void A_dotfile_is_judged_by_its_name_and_not_mistaken_for_having_no_extension()
    {
        File(Path.Combine("here", ".gitkeep"), 0);
        File(Path.Combine("here", ".oddity"), 0);

        var found = Assert.Single(Run(), f => f.Kind == DeadKind.EmptyFile);
        Assert.Equal(".oddity", found.Name);
    }

    [Fact]
    public void An_ordinary_empty_file_is_still_found_next_to_ones_that_are_not()
    {
        File(Path.Combine("pkg", "__init__.py"), 0);
        File(Path.Combine("pkg", "broken.png"), 0);

        var found = Assert.Single(Run(), f => f.Kind == DeadKind.EmptyFile);
        Assert.Equal("broken.png", found.Name);
    }

    [Fact]
    public void The_leftovers_a_file_browser_scatters_about_are_found_even_though_they_have_contents()
    {
        File("Thumbs.db", 4096);
        File(".DS_Store", 6148);

        var found = Run();

        Assert.Equal(2, found.Count);
        Assert.All(found, f => Assert.Equal(DeadKind.Leftover, f.Kind));
    }

    [Fact]
    public void A_folder_holding_only_leftovers_is_still_not_called_empty()
    {
        Folder("cache");
        System.IO.File.WriteAllBytes(Path.Combine(_root, "cache", "Thumbs.db"), new byte[4096]);

        var found = Run();

        // It becomes empty only once the leftover is actually gone, and saying so before that
        // would be claiming something untrue about what is on disk right now.
        Assert.Single(found);
        Assert.Equal(DeadKind.Leftover, found[0].Kind);
    }

    [Fact]
    public void A_shortcut_pointing_at_nothing_is_found_and_a_working_one_is_not()
    {
        var real = File("target.exe", 10);
        Shortcut("works.lnk", real);
        Shortcut("dead.lnk", Path.Combine(_root, "gone.exe"));

        var found = Run();

        var broken = Assert.Single(found, f => f.Kind == DeadKind.BrokenShortcut);
        Assert.Equal("dead.lnk", broken.Name);
        Assert.Contains("gone.exe", broken.Detail);
    }

    [Fact]
    public void A_shortcut_pointing_at_a_folder_that_exists_is_left_alone()
    {
        var folder = Folder("somewhere");
        Shortcut("place.lnk", folder);

        Assert.DoesNotContain(Run(), f => f.Kind == DeadKind.BrokenShortcut);
    }

    [Fact]
    public void A_shortcut_that_cannot_be_read_is_left_alone()
    {
        // Not knowing where it points is not the same as knowing it points at nothing, and only
        // one of those is a safe reason to delete something.
        System.IO.File.WriteAllText(Path.Combine(_root, "damaged.lnk"), "not really a shortcut");

        Assert.DoesNotContain(Run(), f => f.Kind == DeadKind.BrokenShortcut);
    }

    [Fact]
    public void A_shortcut_is_never_reported_as_an_empty_file()
    {
        System.IO.File.WriteAllBytes(Path.Combine(_root, "stub.lnk"), []);

        Assert.Empty(Run());
    }

    [Fact]
    public void A_folder_that_is_skipped_never_makes_its_parent_look_empty()
    {
        Folder("project", "node_modules");

        var found = Run();

        // node_modules is not walked, so nothing is known about what is inside it. Calling the
        // folder above it empty would be a guess, and a destructive one.
        Assert.Empty(found);
    }

    /// <summary>
    /// Reports on the calling thread. The framework's Progress&lt;T&gt; hands the callback to the
    /// thread pool when there is no synchronization context, which is fine for a UI but useless
    /// for a test that has to cancel at an exact point in the walk.
    /// </summary>
    private sealed class Immediately(Action<MouserProgress> act) : IProgress<MouserProgress>
    {
        public void Report(MouserProgress value) => act(value);
    }

    /// <summary>
    /// A folder whose visited part looks empty and whose unvisited part is not. The content lives
    /// in the child that sorts first, so it is pushed first and therefore popped last, which puts
    /// it still in the queue when the sweep is stopped partway through the empty ones.
    /// </summary>
    private string HalfReadFolder(int empties = 200)
    {
        File(Path.Combine("big", "a_has_content", "real.txt"), 40);
        for (var i = 0; i < empties; i++)
            Folder("big", $"e{i:D3}");
        return Path.Combine(_root, "big");
    }

    [Fact]
    public void Stopping_hands_back_what_was_found_rather_than_throwing_it_away()
    {
        HalfReadFolder();
        using var source = new CancellationTokenSource();

        var result = MouserScan.Run(_root, new MouserOptions(),
            new Immediately(_ => source.Cancel()), source.Token);

        Assert.True(result.WasStopped);
        // The empty folders read before the stop are still perfectly good answers.
        Assert.NotEmpty(result.Findings);
        Assert.All(result.Findings, f => Assert.Equal(DeadKind.EmptyFolder, f.Kind));
    }

    [Fact]
    public void Stopping_never_offers_a_folder_it_had_not_finished_reading()
    {
        // Every child read so far is empty, so by the evidence gathered the folder looks empty
        // too. It is not: the one child still in the queue holds a real file. Offering it here
        // would send a full folder to the Recycle Bin.
        var half = HalfReadFolder();
        using var source = new CancellationTokenSource();

        var result = MouserScan.Run(_root, new MouserOptions(),
            new Immediately(_ => source.Cancel()), source.Token);

        Assert.True(result.WasStopped);
        Assert.DoesNotContain(result.Findings, f => f.Path == half);
        // Nor the root above it, which is just as unfinished.
        Assert.DoesNotContain(result.Findings, f => f.Path == _root);
    }

    [Fact]
    public void A_sweep_that_finished_does_not_claim_it_was_stopped()
    {
        Folder("nothing here");

        var result = MouserScan.Run(_root, new MouserOptions(), null, CancellationToken.None);

        Assert.False(result.WasStopped);
        Assert.Single(result.Findings);
    }

    [Fact]
    public void The_same_folder_read_all_the_way_through_is_correctly_left_alone()
    {
        // The other half of the pair above: given the chance to finish, it reaches the child that
        // holds a file and says nothing about the folder at all.
        var half = HalfReadFolder();

        var result = MouserScan.Run(_root, new MouserOptions(), null, CancellationToken.None);

        Assert.False(result.WasStopped);
        Assert.DoesNotContain(result.Findings, f => f.Path == half);
    }

    [Fact]
    public void Stopping_immediately_reports_nothing_at_all()
    {
        Folder("a", "b", "c");
        File("hollow.png", 0);

        using var source = new CancellationTokenSource();
        source.Cancel();

        var result = MouserScan.Run(_root, new MouserOptions(), null, source.Token);

        // Nothing was read, so there is nothing that can honestly be said.
        Assert.True(result.WasStopped);
        Assert.Empty(result.Findings);
    }

    [Fact]
    public void Pointing_at_a_folder_that_is_not_there_finds_nothing_rather_than_throwing()
    {
        var missing = Path.Combine(_root, "not-here");

        Assert.Empty(MouserScan.Run(missing, new MouserOptions(), null, CancellationToken.None).Findings);
    }

    /// <summary>Writes a shortcut file by hand, so the test does not need the Windows shell.</summary>
    private void Shortcut(string name, string target)
    {
        const int headerSize = 0x1C;
        var path = Encoding.Unicode.GetBytes(target);

        var info = new byte[headerSize + path.Length + 2 + 2];
        BitConverter.GetBytes(info.Length).CopyTo(info, 0);
        BitConverter.GetBytes(headerSize).CopyTo(info, 4);
        BitConverter.GetBytes(1).CopyTo(info, 8);
        BitConverter.GetBytes(headerSize).CopyTo(info, 16);
        BitConverter.GetBytes(headerSize + path.Length + 2).CopyTo(info, 24);

        // Written narrow, so the bytes are the ASCII the header claims. Fine for test paths.
        Encoding.Latin1.GetBytes(target).CopyTo(info, headerSize);

        var bytes = new byte[0x4C + info.Length];
        BitConverter.GetBytes(0x4C).CopyTo(bytes, 0);
        BitConverter.GetBytes(2u).CopyTo(bytes, 20);
        info.CopyTo(bytes, 0x4C);

        System.IO.File.WriteAllBytes(Path.Combine(_root, name), bytes);
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
