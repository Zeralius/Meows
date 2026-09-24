using System.Diagnostics;
using System.Globalization;

namespace Meows.Media;

/// <summary>
/// A video's look: the perceptual hash of five frames taken at the same fractions of its length,
/// with its length and size. Two copies of one clip, re-encoded, resized or remuxed, have frames
/// that hash close at the same fractions; two different clips do not.
/// </summary>
public sealed record VideoLook(IReadOnlyList<ulong> Frames, TimeSpan Duration, int Width, int Height)
{
    public long Pixels => (long)Width * Height;
}

/// <summary>
/// Look-alike videos, through ffmpeg, which is a separate install: Meows does not carry a video
/// decoder, and a finder that cannot find ffmpeg says so rather than guessing from file names.
///
/// The frames are taken at 10, 30, 50, 70 and 90 per cent of the length, so a copy with a second
/// of black added at the start still lines up. A copy trimmed by more than a sliver does not, and
/// is not claimed as the same: the lengths have to agree first, which is also what keeps a short
/// clip from matching the long video it was cut from.
/// </summary>
public static class VideoLooks
{
    public static readonly double[] At = [0.1, 0.3, 0.5, 0.7, 0.9];

    private static readonly HashSet<string> Kinds = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".m4v", ".mkv", ".mov", ".avi", ".webm", ".wmv", ".flv", ".mpg", ".mpeg", ".ts", ".m2ts",
    };

    public static bool IsVideo(string path) => Kinds.Contains(Path.GetExtension(path));

    /// <summary>ffmpeg and ffprobe: on PATH, or where the usual Windows installs put them. Null when either is missing.</summary>
    public static (string Ffmpeg, string Ffprobe)? Tools()
    {
        var names = OperatingSystem.IsWindows() ? (".exe", true) : ("", false);
        var folders = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries).ToList();
        if (names.Item2)
        {
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Microsoft", "WinGet", "Links"));
            folders.Add(@"C:\ffmpeg\bin");
            folders.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "ffmpeg", "bin"));
        }

        foreach (var folder in folders)
        {
            try
            {
                var ffmpeg = Path.Combine(folder.Trim('"'), "ffmpeg" + names.Item1);
                var ffprobe = Path.Combine(folder.Trim('"'), "ffprobe" + names.Item1);
                if (File.Exists(ffmpeg) && File.Exists(ffprobe))
                    return (ffmpeg, ffprobe);
            }
            catch (Exception)
            {
            }
        }
        return null;
    }

    /// <summary>A video's look, or null when ffmpeg cannot read it or it has no picture.</summary>
    public static VideoLook? Of(string path, (string Ffmpeg, string Ffprobe) tools, CancellationToken token = default)
    {
        var probe = Run(tools.Ffprobe, token, "-v", "error", "-select_streams", "v:0",
            "-show_entries", "stream=width,height:format=duration", "-of", "default=noprint_wrappers=1", path);
        if (probe is null)
            return null;

        int width = 0, height = 0;
        double seconds = 0;
        foreach (var line in System.Text.Encoding.UTF8.GetString(probe).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var pair = line.Split('=', 2);
            if (pair.Length != 2)
                continue;
            switch (pair[0])
            {
                case "width":
                    int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out width);
                    break;
                case "height":
                    int.TryParse(pair[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out height);
                    break;
                case "duration":
                    double.TryParse(pair[1], NumberStyles.Float, CultureInfo.InvariantCulture, out seconds);
                    break;
            }
        }
        if (width <= 0 || height <= 0 || seconds <= 0)
            return null;

        var frames = new List<ulong>();
        foreach (var fraction in At)
        {
            token.ThrowIfCancellationRequested();
            var png = Frame(path, TimeSpan.FromSeconds(seconds * fraction), tools.Ffmpeg, 160, token);
            if (png is null || PerceptualHash.Of(png) is not { } look)
                return null;
            frames.Add(look.Hash);
        }
        return new VideoLook(frames, TimeSpan.FromSeconds(seconds), width, height);
    }

    /// <summary>One frame as a PNG, small, for hashing or for a preview.</summary>
    public static byte[]? Frame(string path, TimeSpan at, string ffmpeg, int width, CancellationToken token = default) =>
        Run(ffmpeg, token, "-v", "error", "-ss", at.TotalSeconds.ToString("0.###", CultureInfo.InvariantCulture), "-i", path,
            "-frames:v", "1", "-vf", $"scale={width}:-2", "-f", "image2pipe", "-vcodec", "png", "-");

    /// <summary>
    /// How far apart two videos look: the average of their frames' distances, in bits of 64. Null
    /// when their lengths disagree by more than a second or two per cent, which is a different
    /// clip however alike its frames are.
    /// </summary>
    public static int? Distance(VideoLook a, VideoLook b)
    {
        var gap = Math.Abs((a.Duration - b.Duration).TotalSeconds);
        var allowed = Math.Max(1.0, 0.02 * Math.Max(a.Duration.TotalSeconds, b.Duration.TotalSeconds));
        if (gap > allowed || a.Frames.Count != b.Frames.Count || a.Frames.Count == 0)
            return null;
        var total = a.Frames.Zip(b.Frames).Sum(pair => PerceptualHash.Distance(pair.First, pair.Second));
        return (int)Math.Round((double)total / a.Frames.Count);
    }

    private static byte[]? Run(string tool, CancellationToken token, params string[] arguments)
    {
        try
        {
            var start = new ProcessStartInfo(tool)
            {
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
            };
            foreach (var argument in arguments)
                start.ArgumentList.Add(argument);
            using var process = Process.Start(start);
            if (process is null)
                return null;
            using var output = new MemoryStream();
            var copy = process.StandardOutput.BaseStream.CopyToAsync(output, token);
            var errors = process.StandardError.ReadToEndAsync(token);
            if (!process.WaitForExit(60_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            copy.Wait(token);
            errors.Wait(token);
            return process.ExitCode == 0 && output.Length > 0 ? output.ToArray() : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
