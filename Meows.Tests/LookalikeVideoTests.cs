using System.Diagnostics;
using Meows.Media;
using Meows.Plugins.Purrge.Services;

namespace Meows.Tests;

/// <summary>
/// Look-alike videos: the same clip re-encoded at another size and in another format is found,
/// a different clip is not, lengths that disagree are never the same video, and without ffmpeg
/// nothing is guessed. The videos are made by ffmpeg itself, so on a machine without it the
/// tests that need one say nothing.
/// </summary>
public sealed class LookalikeVideoTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-vids-" + Guid.NewGuid().ToString("N")[..8]);

    public LookalikeVideoTests() => Directory.CreateDirectory(_root);

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

    private string Make(string name, string source, params string[] extra)
    {
        var path = Path.Combine(_root, name);
        var start = new ProcessStartInfo(VideoLooks.Tools()!.Value.Ffmpeg) { RedirectStandardError = true, RedirectStandardOutput = true };
        foreach (var a in (string[])["-v", "error", .. (source.StartsWith("lavfi:") ? ["-f", "lavfi", "-i", source[6..]] : new[] { "-i", source }), .. extra, path])
            start.ArgumentList.Add(a);
        using var process = Process.Start(start)!;
        process.WaitForExit();
        return path;
    }

    [Fact]
    public void Lengths_that_disagree_are_never_the_same_video()
    {
        ulong[] frames = [1, 2, 3, 4, 5];
        var a = new VideoLook(frames, TimeSpan.FromSeconds(100), 1920, 1080);

        Assert.Equal(0, VideoLooks.Distance(a, a with { Duration = TimeSpan.FromSeconds(101.5) }));
        Assert.Null(VideoLooks.Distance(a, a with { Duration = TimeSpan.FromSeconds(103) }));
        Assert.Null(VideoLooks.Distance(a, a with { Duration = TimeSpan.FromSeconds(10) }));
        Assert.Equal(2, VideoLooks.Distance(a, a with { Frames = [1 ^ 0b11, 2 ^ 0b11, 3 ^ 0b11, 4 ^ 0b11, 5 ^ 0b11] }));
    }

    [Fact]
    public void The_same_clip_re_encoded_is_found_and_a_different_one_is_not()
    {
        if (VideoLooks.Tools() is not { } tools)
            return;

        var original = Make("clip.mp4", "lavfi:testsrc=duration=8:size=640x360:rate=25", "-c:v", "libx264", "-pix_fmt", "yuv420p");
        var small = Make("clip-small.mkv", original, "-vf", "scale=320:180", "-c:v", "libx264", "-crf", "35");
        Make("other.mp4", "lavfi:mandelbrot=size=640x360:rate=25", "-t", "8", "-c:v", "libx264", "-pix_fmt", "yuv420p");
        Make("short.mp4", original, "-t", "3", "-c:v", "libx264");

        var look = VideoLooks.Of(original, tools)!;
        Assert.Equal((640, 360), (look.Width, look.Height));
        Assert.Equal(5, look.Frames.Count);
        Assert.InRange(look.Duration.TotalSeconds, 7.5, 8.5);
        Assert.Null(VideoLooks.Of(Path.Combine(_root, "not-a-video.mp4"), tools));

        var sets = LookalikeScanner.ScanVideos(_root, new ScanOptions(MinimumBytes: 0, SkipSystemFolders: false), LookalikeScanner.DefaultThreshold, tools, null, CancellationToken.None);

        var set = Assert.Single(sets);
        Assert.Equal([original, small], set.Files.Select(f => f.Path));
        Assert.True(set.Best.IsVideo);
        Assert.NotNull(VideoLooks.Frame(original, TimeSpan.FromSeconds(4), tools.Ffmpeg, 200));
    }
}
