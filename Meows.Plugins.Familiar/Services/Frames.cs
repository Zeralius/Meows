using System.Reflection;
using System.Text.Json;
using SkiaSharp;

namespace Meows.Plugins.Familiar.Services;

/// <summary>Where a frame's pixels come from: a file on disk when the user dropped it in, the assembly otherwise.</summary>
public interface IFrame
{
    string File { get; }

    string Label { get; }

    /// <summary>Absolute path for a frame from the user's Frames folder; null for one that ships embedded.</summary>
    string? Path { get; }
}

/// <summary>A ring for a token: a square PNG with a transparent middle and a transparent outside.</summary>
public sealed record TokenFrame(string File, string Label, int Size, int InnerRadius, string? Path = null) : IFrame
{
    /// <summary>The token is cut to this circle, centred, so nothing shows outside the ring.</summary>
    public float InnerRadiusFraction => (float)InnerRadius / Size;
}

/// <summary>A disc drawn behind the portrait, inside the ring, for a picture with a transparent or ugly background.</summary>
public sealed record BackgroundFrame(string File, string Label, string? Path = null) : IFrame;

/// <summary>A border for a map or a handout: a 9-slice tile whose corners and edges go round a canvas.</summary>
public sealed record BorderFrame(string File, string Label, int Size, int Slice, string? Path = null) : IFrame;

/// <summary>
/// The frames on offer: the set that ships embedded, plus whatever is in the Frames folder under
/// the kits root, so a better ring is a PNG dropped in a folder rather than a rebuild. A user
/// file with the same name as a shipped one replaces it.
/// </summary>
public sealed class FrameSet
{
    /// <summary>The folder under the kits root that user frames are read from.</summary>
    public const string UserFolderName = "Frames";

    private static readonly Lazy<FrameSet> Embedded = new(() => Load(Open("frames.json"), null));

    private FrameSet(IReadOnlyList<TokenFrame> tokens, IReadOnlyList<BackgroundFrame> backgrounds, IReadOnlyList<BorderFrame> borders)
    {
        Tokens = tokens;
        Backgrounds = backgrounds;
        Borders = borders;
    }

    public IReadOnlyList<TokenFrame> Tokens { get; }

    public IReadOnlyList<BackgroundFrame> Backgrounds { get; }

    public IReadOnlyList<BorderFrame> Borders { get; }

    public TokenFrame? Token(string? file) => Tokens.FirstOrDefault(t => string.Equals(t.File, file, StringComparison.OrdinalIgnoreCase));

    public BackgroundFrame? Background(string? file) => Backgrounds.FirstOrDefault(b => string.Equals(b.File, file, StringComparison.OrdinalIgnoreCase));

    public BorderFrame? Border(string? file) => Borders.FirstOrDefault(b => string.Equals(b.File, file, StringComparison.OrdinalIgnoreCase));

    /// <summary>The frames that ship with the plugin, and nothing else.</summary>
    public static FrameSet Shipped => Embedded.Value;

    /// <summary>
    /// The shipped set plus the user's folder. A <c>frames.json</c> there is read the same way
    /// as the embedded one; a PNG without an entry is taken by its name: <c>token-*.png</c> is a
    /// ring with the inner radius measured from the picture, <c>background-*.png</c> a disc, and
    /// <c>border-*.png</c> a 9-slice tile with a slice a third of its width.
    /// </summary>
    public static FrameSet WithUserFolder(string? folder)
    {
        var shipped = Shipped;
        if (string.IsNullOrEmpty(folder) || !Directory.Exists(folder))
            return shipped;

        var tokens = shipped.Tokens.ToList();
        var backgrounds = shipped.Backgrounds.ToList();
        var borders = shipped.Borders.ToList();

        FrameSet listed;
        try
        {
            var json = System.IO.Path.Combine(folder, "frames.json");
            listed = System.IO.File.Exists(json) ? Load(System.IO.File.OpenRead(json), folder) : new FrameSet([], [], []);
        }
        catch (Exception)
        {
            listed = new FrameSet([], [], []);
        }

        foreach (var token in listed.Tokens) Replace(tokens, token);
        foreach (var background in listed.Backgrounds) Replace(backgrounds, background);
        foreach (var border in listed.Borders) Replace(borders, border);

        foreach (var png in Directory.EnumerateFiles(folder, "*.png").OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
        {
            var file = System.IO.Path.GetFileName(png);
            var label = Labelled(file);
            try
            {
                if (file.StartsWith("token-", StringComparison.OrdinalIgnoreCase) && listed.Token(file) is null)
                {
                    using var bitmap = SKBitmap.Decode(png);
                    if (bitmap is not null)
                        Replace(tokens, new TokenFrame(file, label, bitmap.Width, MeasureInnerRadius(bitmap), png));
                }
                else if (file.StartsWith("background-", StringComparison.OrdinalIgnoreCase) && listed.Background(file) is null)
                {
                    Replace(backgrounds, new BackgroundFrame(file, label, png));
                }
                else if (file.StartsWith("border-", StringComparison.OrdinalIgnoreCase) && listed.Border(file) is null)
                {
                    using var bitmap = SKBitmap.Decode(png);
                    if (bitmap is not null)
                        Replace(borders, new BorderFrame(file, label, bitmap.Width, Math.Max(1, bitmap.Width / 3), png));
                }
            }
            catch (Exception)
            {
                // A PNG that does not decode is not a frame; the rest of the folder still counts.
            }
        }

        return new FrameSet(tokens, backgrounds, borders);
    }

    /// <summary>
    /// How far the transparent middle of a ring reaches: from the centre outwards along four
    /// spokes until the ring is met, the shortest of them, so an off-centre ring still cuts
    /// inside the paint.
    /// </summary>
    public static int MeasureInnerRadius(SKBitmap ring)
    {
        var cx = ring.Width / 2;
        var cy = ring.Height / 2;
        var limit = Math.Min(cx, cy);
        var shortest = limit;
        foreach (var (dx, dy) in new[] { (1, 0), (-1, 0), (0, 1), (0, -1) })
        {
            for (var r = 0; r < limit; r++)
            {
                if (ring.GetPixel(cx + dx * r, cy + dy * r).Alpha > 32)
                {
                    shortest = Math.Min(shortest, r);
                    break;
                }
            }
        }
        return Math.Max(1, shortest - 1);
    }

    private static void Replace<T>(List<T> list, T frame) where T : IFrame
    {
        var i = list.FindIndex(f => string.Equals(f.File, frame.File, StringComparison.OrdinalIgnoreCase));
        if (i >= 0)
            list[i] = frame;
        else
            list.Add(frame);
    }

    /// <summary>"token-dark-gold.png" reads as "Dark gold" on the card.</summary>
    private static string Labelled(string file)
    {
        var stem = System.IO.Path.GetFileNameWithoutExtension(file);
        var dash = stem.IndexOf('-');
        var rest = dash >= 0 ? stem[(dash + 1)..] : stem;
        rest = rest.Replace('-', ' ').Replace('_', ' ').Trim();
        return rest.Length == 0 ? stem : char.ToUpperInvariant(rest[0]) + rest[1..];
    }

    /// <summary>The frame's pixels, decoded fresh each time; a frame is small and a bitmap is disposable.</summary>
    public static SKBitmap Bitmap(IFrame frame)
    {
        using var stream = frame.Path is { } path
            ? System.IO.File.OpenRead(path)
            : Open(frame.File) ?? throw new FileNotFoundException($"frame {frame.File} is not embedded");
        return SKBitmap.Decode(stream) ?? throw new InvalidDataException($"frame {frame.File} did not decode");
    }

    private static FrameSet Load(Stream? stream, string? folder)
    {
        if (stream is null)
            return new FrameSet([], [], []);

        using (stream)
        using (var document = JsonDocument.Parse(stream))
        {
            var root = document.RootElement;
            string? PathFor(string file) => folder is null ? null : System.IO.Path.Combine(folder, file);

            var tokens = new List<TokenFrame>();
            if (root.TryGetProperty("tokens", out var ts))
                foreach (var t in ts.EnumerateArray())
                    tokens.Add(new TokenFrame(t.GetProperty("file").GetString()!, t.GetProperty("label").GetString()!,
                        t.GetProperty("size").GetInt32(), t.GetProperty("innerRadius").GetInt32(), PathFor(t.GetProperty("file").GetString()!)));

            var backgrounds = new List<BackgroundFrame>();
            if (root.TryGetProperty("backgrounds", out var bgs))
                foreach (var b in bgs.EnumerateArray())
                    backgrounds.Add(new BackgroundFrame(b.GetProperty("file").GetString()!, b.GetProperty("label").GetString()!,
                        PathFor(b.GetProperty("file").GetString()!)));

            var borders = new List<BorderFrame>();
            if (root.TryGetProperty("borders", out var bs))
                foreach (var b in bs.EnumerateArray())
                    borders.Add(new BorderFrame(b.GetProperty("file").GetString()!, b.GetProperty("label").GetString()!,
                        b.GetProperty("size").GetInt32(), b.GetProperty("slice").GetInt32(), PathFor(b.GetProperty("file").GetString()!)));

            return new FrameSet(tokens, backgrounds, borders);
        }
    }

    private static Stream? Open(string file)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("Frames." + file, StringComparison.OrdinalIgnoreCase));
        return name is null ? null : assembly.GetManifestResourceStream(name);
    }
}
