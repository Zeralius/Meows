using System.IO.Compression;
using System.Text;
using Meows.Bot;
using Meows.Disk;
using Meows.Plugins.Kibble.Services;
using Meows.Plugins.Portion.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Files whose extension lies, told by their first bytes. The verdict is only given when both
/// the name and the bytes are known; Portion shows it and renames to match, Kibble puts the name
/// right on the way in or refuses what the bot would never post.
/// </summary>
public sealed class ManxTests : IDisposable
{
    private readonly TempWorkspace _temp = new();

    public ManxTests()
    {
        _temp.WriteConfig(_temp.AddGroup("Alpha"));
    }

    public void Dispose() => _temp.Dispose();

    private GroupConfig Alpha => _temp.Workspace.LoadConfig().Groups[0];

    private string Loose(string name, byte[] bytes)
    {
        var folder = Path.Combine(_temp.Root, "loose");
        Directory.CreateDirectory(folder);
        var path = Path.Combine(folder, name);
        File.WriteAllBytes(path, bytes);
        return path;
    }

    private static readonly byte[] LoginPage = Encoding.UTF8.GetBytes("﻿\n  <!DOCTYPE html><html><body>Please sign in</body></html>");

    private static byte[] Rar => [.. "Rar!\x1A\x07\x01\x00"u8, .. new byte[40]];

    private static byte[] Mp4 => [0, 0, 0, 0x20, .. "ftypisom"u8, .. new byte[40]];

    private static byte[] Zip()
    {
        using var buffer = new MemoryStream();
        using (var archive = new ZipArchive(buffer, ZipArchiveMode.Create, leaveOpen: true))
        {
            var entry = archive.CreateEntry("01.jpg");
            using var stream = entry.Open();
            stream.Write(TestPictures.Jpeg());
        }
        return buffer.ToArray();
    }

    [Fact]
    public void The_first_bytes_say_what_a_file_is()
    {
        Assert.Equal("JPEG", FileSniff.Of(TestPictures.Jpeg())?.Name);
        Assert.Equal("PNG", FileSniff.Of(TestPictures.Png())?.Name);
        Assert.Equal("WebP", FileSniff.Of(TestPictures.WebP())?.Name);
        Assert.Equal("GIF", FileSniff.Of("GIF89a\x01\x00"u8)?.Name);
        Assert.Equal("MP4", FileSniff.Of(Mp4)?.Name);
        Assert.Equal("QuickTime", FileSniff.Of([0, 0, 0, 0x14, .. "ftypqt  "u8, 0, 0, 0, 0])?.Name);
        Assert.Equal("ZIP", FileSniff.Of(Zip())?.Name);
        Assert.Equal("RAR", FileSniff.Of(Rar)?.Name);
        Assert.Equal("PDF", FileSniff.Of("%PDF-1.7\n"u8)?.Name);
        Assert.Equal("a web page", FileSniff.Of(LoginPage)?.Name);
        Assert.Null(FileSniff.Of("just some text"u8));
        Assert.Null(FileSniff.Of([]));
    }

    [Theory]
    [InlineData("a.jpg", "jpeg")]
    [InlineData("a.jpeg", "jpeg")]
    [InlineData("a.jfif", "jpeg")]
    [InlineData("a.cbz", "zip")]
    [InlineData("a.mov", "mp4")]
    [InlineData("a.txt", "html")]
    [InlineData("a", "png")]
    [InlineData("a.jpg", "text")]
    public void An_honest_name_an_unjudged_name_or_unknown_bytes_is_never_called_a_lie(string name, string content)
    {
        byte[] bytes = content switch
        {
            "jpeg" => TestPictures.Jpeg(),
            "zip" => Zip(),
            "mp4" => Mp4,
            "html" => LoginPage,
            "png" => TestPictures.Png(),
            _ => "hello"u8.ToArray(),
        };
        Assert.Null(FileSniff.Check(name, bytes));
    }

    [Fact]
    public void A_name_that_lies_is_caught_and_says_what_it_should_have_been()
    {
        var png = FileSniff.Check("page.jpg", TestPictures.Png());
        Assert.NotNull(png);
        Assert.Equal("jpg", png!.Says);
        Assert.Equal("PNG", png.Is.Name);
        Assert.Equal(Path.Combine("q", "page.png"), png.RenamedPath(Path.Combine("q", "page.jpg")));

        Assert.Equal("a web page", FileSniff.Check("photo.PNG", LoginPage)?.Is.Name);
        Assert.Equal("RAR", FileSniff.Check("comic.cbz", Rar)?.Is.Name);
    }

    [Fact]
    public void Portion_calls_a_png_named_jpg_a_note_and_offers_the_rename()
    {
        var queue = _temp.Workspace.ToSendFolder(Alpha);
        Directory.CreateDirectory(queue);
        var path = Path.Combine(queue, "page.jpg");
        File.WriteAllBytes(path, TestPictures.Png());

        var heavy = Weigher.Inspect(Alpha, path);

        Assert.NotNull(heavy);
        Assert.Equal([Trouble.WrongExtension], heavy!.Troubles);
        Assert.False(heavy.WillFail);
        Assert.Equal(Path.Combine(queue, "page.png"), heavy.HonestPath);
        var row = new HeavyViewModel(heavy);
        Assert.True(row.CanRename);
        Assert.Contains("Says jpg, is PNG", row.TroubleText);
    }

    [Fact]
    public void Portion_calls_a_login_page_named_png_and_a_rar_named_cbz_failures_to_hold_not_rename()
    {
        var queue = _temp.Workspace.ToSendFolder(Alpha);
        Directory.CreateDirectory(queue);
        var page = Path.Combine(queue, "photo.png");
        File.WriteAllBytes(page, LoginPage);
        var comic = Path.Combine(queue, "issue.cbz");
        File.WriteAllBytes(comic, Rar);

        foreach (var path in new[] { page, comic })
        {
            var heavy = Weigher.Inspect(Alpha, path);
            Assert.NotNull(heavy);
            Assert.Contains(Trouble.WrongExtension, heavy!.Troubles);
            Assert.True(heavy.WillFail);
            Assert.False(heavy.CanShrink);
            Assert.Null(heavy.HonestPath);
            Assert.False(new HeavyViewModel(heavy).CanRename);
        }
    }

    [Fact]
    public void Renaming_to_match_keeps_the_date_and_will_not_write_over_anything()
    {
        var queue = _temp.Workspace.ToSendFolder(Alpha);
        Directory.CreateDirectory(queue);
        var path = Path.Combine(queue, "page.jpg");
        File.WriteAllBytes(path, TestPictures.Png());
        var stamp = new DateTime(2025, 1, 2, 3, 4, 5, DateTimeKind.Utc);
        File.SetLastWriteTimeUtc(path, stamp);

        File.WriteAllBytes(Path.Combine(queue, "page.png"), TestPictures.Png(10, 10));
        var (blocked, why) = Weigher.RenameToMatch(path);
        Assert.Null(blocked);
        Assert.Contains("page.png", why);
        Assert.True(File.Exists(path));

        File.Delete(Path.Combine(queue, "page.png"));
        var (renamed, error) = Weigher.RenameToMatch(path);
        Assert.Null(error);
        Assert.Equal(Path.Combine(queue, "page.png"), renamed);
        Assert.False(File.Exists(path));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(renamed!));

        var (again, honest) = Weigher.RenameToMatch(renamed!);
        Assert.Null(again);
        Assert.NotNull(honest);
    }

    [Fact]
    public void Kibble_queues_a_lying_picture_under_its_honest_name_and_says_so()
    {
        var source = Loose("page.jpg", TestPictures.Png());

        var result = Intake.Send(source, _temp.Workspace, Alpha, IntakeStamp.KeepSource);

        Assert.Equal(IntakeOutcome.Sent, result.Outcome);
        Assert.Equal("page.png", Path.GetFileName(result.Destination));
        Assert.Contains("page.png", result.Detail);
        Assert.True(File.Exists(result.Destination));
        Assert.Null(Weigher.Inspect(Alpha, result.Destination!));
    }

    [Fact]
    public void Kibble_refuses_what_the_bot_would_never_post_whatever_its_name_says()
    {
        var page = Loose("photo.png", LoginPage);
        var comic = Loose("issue.cbz", Rar);

        foreach (var path in new[] { page, comic })
        {
            var result = Intake.Send(path, _temp.Workspace, Alpha, IntakeStamp.KeepSource);
            Assert.Equal(IntakeOutcome.NotPostable, result.Outcome);
            Assert.True(result.WouldFail);
            Assert.True(File.Exists(path));
        }

        var refused = Intake.Send(page, _temp.Workspace, Alpha, IntakeStamp.KeepSource);
        Assert.Contains("a web page", refused.Detail);

        var good = Loose("good.jpg", TestPictures.Jpeg());
        var bundle = Intake.InspectBundle([good, Loose("two.jpg", TestPictures.Jpeg()), page]);
        Assert.NotNull(bundle);
        Assert.Equal(page, bundle!.SourcePath);
    }
}
