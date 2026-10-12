using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// The shared way back from the Recycle Bin: a plugin records what it recycled, and History's
/// Put back restores it whether that plugin is open or not. These go through the real bin, as
/// the delete tests do.
/// </summary>
public sealed class RecycledPutBackTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "putback-" + Guid.NewGuid().ToString("N")[..10]);

    public RecycledPutBackTests()
    {
        Directory.CreateDirectory(_root);
        TestStrings.Install();
    }

    private static StoredEvent Line(string kind, string subject, IReadOnlyDictionary<string, string>? data = null) =>
        new(1, DateTime.Now, "meows.test", kind, subject, null, data ?? new Dictionary<string, string>());

    [Fact]
    public void A_line_names_its_paths_in_its_data_or_its_subject()
    {
        Assert.Equal([@"C:\a.txt", @"C:\b.txt"], Recycled.PathsOf(Line("recycled", "2", new Dictionary<string, string> { ["paths"] = "C:\\a.txt\nC:\\b.txt" })));
        Assert.Equal([@"C:\a.txt"], Recycled.PathsOf(Line("recycled", @"C:\a.txt")));
        Assert.Empty(Recycled.PathsOf(Line("recycled", "3 screenshots")));
        Assert.Empty(Recycled.PathsOf(Line("sent", @"C:\a.txt")));
    }

    [Fact]
    public void What_went_to_the_bin_comes_back_from_History_with_no_plugin_open()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var file = Path.Combine(_root, "keep-me.txt");
        File.WriteAllText(file, "precious");
        Assert.True(RecycleBin.Send([file]).Succeeded);
        Assert.False(File.Exists(file));

        var line = Line("recycled", "1", new Dictionary<string, string> { ["paths"] = file });
        var putBack = new PutBack(plugin: null);

        Assert.True(putBack.CanUndo(line));
        Assert.Null(putBack.Undo(line));
        Assert.Equal("precious", File.ReadAllText(file));

        // Back where it was, so nothing is left to put back.
        Assert.False(putBack.CanUndo(line));
    }

    [Fact]
    public void Nothing_is_put_back_on_top_of_something_new()
    {
        if (!OperatingSystem.IsWindows())
            return;

        var file = Path.Combine(_root, "taken.txt");
        File.WriteAllText(file, "old");
        RecycleBin.Send([file]);
        File.WriteAllText(file, "new");

        var line = Line("recycled", file);

        Assert.False(new PutBack(null).CanUndo(line));
        Assert.Equal("new", File.ReadAllText(file));
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
