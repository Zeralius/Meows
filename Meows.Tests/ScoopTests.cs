using System.Text;
using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Scoop.ViewModels;

namespace Meows.Tests;

/// <summary>
/// The Recycle Bin read rather than deleted into. The format is the part worth pinning: a
/// misread path length gives a plausible-looking path, and a restore would then put the file
/// somewhere nobody asked for.
/// </summary>
public sealed class ScoopTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-scoop-" + Guid.NewGuid().ToString("N")[..8]);

    public ScoopTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    /// <summary>A $I file as Windows 10 writes one: version 2, size, FILETIME, path length, path.</summary>
    private static byte[] Modern(string path, long size, DateTime deletedAt)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(2L));
        bytes.AddRange(BitConverter.GetBytes(size));
        bytes.AddRange(BitConverter.GetBytes(deletedAt.ToFileTime()));
        bytes.AddRange(BitConverter.GetBytes(path.Length + 1));
        bytes.AddRange(Encoding.Unicode.GetBytes(path + "\0"));
        return bytes.ToArray();
    }

    /// <summary>The older one, where the path is a fixed 260 characters padded with nulls.</summary>
    private static byte[] Legacy(string path, long size, DateTime deletedAt)
    {
        var bytes = new List<byte>();
        bytes.AddRange(BitConverter.GetBytes(1L));
        bytes.AddRange(BitConverter.GetBytes(size));
        bytes.AddRange(BitConverter.GetBytes(deletedAt.ToFileTime()));
        var padded = path.PadRight(260, '\0');
        bytes.AddRange(Encoding.Unicode.GetBytes(padded));
        return bytes.ToArray();
    }

    /// <summary>Writes a $I and its $R into a bin folder, the way a deletion leaves them.</summary>
    private string Bin(params (string Path, long Size, DateTime When, bool Legacy)[] items)
    {
        var bin = Path.Combine(_root, "bin");
        Directory.CreateDirectory(bin);
        var id = 0;
        foreach (var item in items)
        {
            var name = $"ABC{id++:000}" + Path.GetExtension(item.Path);
            File.WriteAllBytes(Path.Combine(bin, "$I" + name),
                item.Legacy ? Legacy(item.Path, item.Size, item.When) : Modern(item.Path, item.Size, item.When));
            File.WriteAllText(Path.Combine(bin, "$R" + name), "contents");
        }

        return bin;
    }

    [Fact]
    public void Both_metadata_formats_give_back_the_path_the_size_and_the_time()
    {
        var when = new DateTime(2026, 3, 4, 17, 30, 0, DateTimeKind.Local);

        foreach (var bytes in new[]
                 {
                     Modern(@"F:\Downloads\holiday.zip", 4096, when),
                     Legacy(@"F:\Downloads\holiday.zip", 4096, when),
                 })
        {
            var parsed = RecycleBinContents.ParseMetadata(bytes);

            Assert.NotNull(parsed);
            Assert.Equal(@"F:\Downloads\holiday.zip", parsed.Value.Path);
            Assert.Equal(4096, parsed.Value.Size);
            Assert.Equal(when, parsed.Value.DeletedAt);
        }
    }

    [Fact]
    public void A_path_with_an_umlaut_survives_the_read()
    {
        // The bin stores UTF-16 and the length is in characters, so a wrong unit here would cut
        // the path short exactly where a non-ASCII name starts.
        var parsed = RecycleBinContents.ParseMetadata(Modern(@"F:\Bücher\Größe.pdf", 10, DateTime.Now));

        Assert.NotNull(parsed);
        Assert.Equal(@"F:\Bücher\Größe.pdf", parsed.Value.Path);
    }

    [Theory]
    [InlineData(3)]   // A version that does not exist.
    [InlineData(0)]
    public void A_header_that_is_not_a_known_version_is_refused(long version)
    {
        var bytes = Modern(@"F:\x\a.txt", 1, DateTime.Now);
        BitConverter.GetBytes(version).CopyTo(bytes, 0);

        Assert.Null(RecycleBinContents.ParseMetadata(bytes));
    }

    [Fact]
    public void A_truncated_file_and_a_lying_length_are_both_refused()
    {
        var good = Modern(@"F:\x\a.txt", 1, DateTime.Now);

        Assert.Null(RecycleBinContents.ParseMetadata(good.AsSpan(0, 20)));

        // The length says far more characters than the file holds, which is the case that would
        // read past the end or, worse, produce a path from whatever came after it.
        var lying = (byte[])good.Clone();
        BitConverter.GetBytes(9999).CopyTo(lying, 24);
        Assert.Null(RecycleBinContents.ParseMetadata(lying));
    }

    [Fact]
    public void A_stamp_that_is_not_a_filetime_still_gives_a_row()
    {
        var bytes = Modern(@"F:\x\a.txt", 1, DateTime.Now);
        BitConverter.GetBytes(long.MaxValue).CopyTo(bytes, 16);

        var parsed = RecycleBinContents.ParseMetadata(bytes);

        // The item is real and still taking up room; only the time is unknown.
        Assert.NotNull(parsed);
        Assert.Equal(DateTime.MinValue, parsed.Value.DeletedAt);
    }

    [Fact]
    public void A_bin_folder_reads_as_one_row_per_pair()
    {
        var bin = Bin(
            (@"F:\Downloads\big.iso", 8_000_000_000, DateTime.Now.AddDays(-400), false),
            (@"F:\Pictures\cat.png", 2048, DateTime.Now.AddDays(-1), true));

        var items = RecycleBinContents.Read(bin);

        Assert.Equal(2, items.Count);
        var big = items.Single(i => i.Name == "big.iso");
        Assert.Equal(@"F:\Downloads", big.OriginalFolder);
        Assert.Equal(8_000_000_000, big.Size);
        Assert.False(big.IsFolder);
    }

    [Fact]
    public void Metadata_with_no_data_beside_it_is_not_a_row()
    {
        // Half a pair is not something anyone can restore or count, and counting it would
        // inflate the headline number the whole tab exists for.
        var bin = Bin((@"F:\x\a.txt", 10, DateTime.Now, false));
        File.Delete(Directory.GetFiles(bin, "$R*").Single());

        Assert.Empty(RecycleBinContents.Read(bin));
    }

    [Fact]
    public void One_unreadable_entry_costs_its_own_row_and_nothing_else()
    {
        var bin = Bin(
            (@"F:\x\good.txt", 10, DateTime.Now, false),
            (@"F:\x\bad.txt", 10, DateTime.Now, false));
        var bad = Directory.GetFiles(bin, "$I*").First(f => f.EndsWith("001.txt"));
        File.WriteAllBytes(bad, [1, 2, 3]);

        var items = RecycleBinContents.Read(bin);

        Assert.Equal("good.txt", Assert.Single(items).Name);
    }

    [Fact]
    public void Restoring_puts_the_file_back_and_takes_the_metadata_with_it()
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(home);
        var bin = Bin((Path.Combine(home, "notes.txt"), 8, DateTime.Now, false));
        var item = RecycleBinContents.Read(bin).Single();

        Assert.Null(RecycleBinContents.Restore(item));

        Assert.True(File.Exists(Path.Combine(home, "notes.txt")));
        Assert.False(File.Exists(item.MetadataPath));
        Assert.Empty(RecycleBinContents.Read(bin));
    }

    [Fact]
    public void Restoring_onto_something_that_is_there_now_is_refused_rather_than_overwriting()
    {
        var home = Path.Combine(_root, "home");
        Directory.CreateDirectory(home);
        var target = Path.Combine(home, "notes.txt");
        File.WriteAllText(target, "the newer one");

        var bin = Bin((target, 8, DateTime.Now, false));
        var item = RecycleBinContents.Read(bin).Single();

        Assert.True(item.IsBlocked);
        Assert.NotNull(RecycleBinContents.Restore(item));
        Assert.Equal("the newer one", File.ReadAllText(target));
        Assert.Single(RecycleBinContents.Read(bin));
    }

    [Fact]
    public void Restoring_into_a_folder_that_has_gone_makes_the_folder_again()
    {
        var gone = Path.Combine(_root, "gone", "deeper");
        var bin = Bin((Path.Combine(gone, "notes.txt"), 8, DateTime.Now, false));
        var item = RecycleBinContents.Read(bin).Single();

        Assert.Null(RecycleBinContents.Restore(item));
        Assert.True(File.Exists(Path.Combine(gone, "notes.txt")));
    }

    [Fact]
    public void The_drive_line_adds_up_and_the_oldest_is_the_oldest()
    {
        var older = DateTime.Now.AddDays(-400);
        var drive = new BinDrive("F:\\", RecycleBinContents.Read(Bin(
            (@"F:\a.txt", 100, DateTime.Now, false),
            (@"F:\b.txt", 250, older, false))));

        Assert.Equal(350, drive.Bytes);
        Assert.Equal(2, drive.Count);
        Assert.Equal(older.ToString("s"), drive.Oldest?.ToString("s"));
    }

    [Fact]
    public void The_tab_says_nothing_on_Home_until_it_has_read_something()
    {
        var host = new FakeHost(Path.Combine(_root, "host"));
        using var model = new ScoopViewModel(host);

        // The read goes through background work, which the fake records without running, so
        // this is the state a tab is in for the first moment it is open.
        Assert.Null(((IGlanceable)model).Glance());
        Assert.Empty(model.Search("iso", 5));
    }

    [Fact]
    public void Reading_this_machine_answers_for_every_drive_without_throwing()
    {
        // The only test here that looks at the real bins. It cannot assert what is in them,
        // which differs per machine and per day, but it can assert the thing that would
        // actually break: a drive that will not open, a bin belonging to nobody, or a $I file
        // written by a Windows nobody tested against must all come back as a row rather than an
        // exception out of a background task.
        var drives = RecycleBinContents.ReadAll();

        if (!OperatingSystem.IsWindows())
            return;

        Assert.All(drives, drive =>
        {
            Assert.False(string.IsNullOrWhiteSpace(drive.Root));
            Assert.True(drive.Bytes >= 0);
            Assert.All(drive.Items, item => Assert.False(string.IsNullOrWhiteSpace(item.OriginalPath)));
        });
    }

    [Fact]
    public void Emptying_a_drive_asks_once_before_it_does_anything()
    {
        var host = new FakeHost(Path.Combine(_root, "host"));
        using var model = new ScoopViewModel(host);

        // Nothing is selected, so the verb is unavailable rather than merely unconfirmed: the
        // one irreversible thing in Meows never starts from an empty selection.
        Assert.False(model.EmptyCommand.CanExecute(null));
        Assert.False(model.IsConfirmingEmpty);
    }
}
