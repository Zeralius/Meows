using System.Reflection;
using System.Text.Json;
using SkiaSharp;

namespace Meows.Plugins.Kit.Services;

/// <summary>A ring for a token: a square PNG with a transparent middle and a transparent outside.</summary>
public sealed record TokenFrame(string File, string Label, int Size, int InnerRadius)
{
    /// <summary>The token is cut to this circle, centred, so nothing shows outside the ring.</summary>
    public float InnerRadiusFraction => (float)InnerRadius / Size;
}

/// <summary>A border for a map or a handout: a 9-slice tile whose corners and edges go round a canvas.</summary>
public sealed record BorderFrame(string File, string Label, int Size, int Slice);

/// <summary>
/// The frames that ship with the plugin, as embedded PNGs with a small json saying what each one
/// is. Simple ones, drawn to prove the cutting and the framing; a nicer set is a matter of
/// dropping better PNGs into the Frames folder with the same names.
/// </summary>
public static class Frames
{
    private static readonly Lazy<(IReadOnlyList<TokenFrame> Tokens, IReadOnlyList<BorderFrame> Borders)> Loaded = new(Load);

    public static IReadOnlyList<TokenFrame> Tokens => Loaded.Value.Tokens;

    public static IReadOnlyList<BorderFrame> Borders => Loaded.Value.Borders;

    public static TokenFrame? Token(string? file) => Tokens.FirstOrDefault(t => t.File == file);

    public static BorderFrame? Border(string? file) => Borders.FirstOrDefault(b => b.File == file);

    /// <summary>The frame's pixels, decoded fresh each time; a frame is small and a bitmap is disposable.</summary>
    public static SKBitmap Bitmap(string file)
    {
        using var stream = Open(file) ?? throw new FileNotFoundException($"frame {file} is not embedded");
        return SKBitmap.Decode(stream) ?? throw new InvalidDataException($"frame {file} did not decode");
    }

    private static (IReadOnlyList<TokenFrame>, IReadOnlyList<BorderFrame>) Load()
    {
        using var stream = Open("frames.json");
        if (stream is null)
            return ([], []);

        using var document = JsonDocument.Parse(stream);
        var root = document.RootElement;

        var tokens = new List<TokenFrame>();
        foreach (var t in root.GetProperty("tokens").EnumerateArray())
            tokens.Add(new TokenFrame(t.GetProperty("file").GetString()!, t.GetProperty("label").GetString()!,
                t.GetProperty("size").GetInt32(), t.GetProperty("innerRadius").GetInt32()));

        var borders = new List<BorderFrame>();
        foreach (var b in root.GetProperty("borders").EnumerateArray())
            borders.Add(new BorderFrame(b.GetProperty("file").GetString()!, b.GetProperty("label").GetString()!,
                b.GetProperty("size").GetInt32(), b.GetProperty("slice").GetInt32()));

        return (tokens, borders);
    }

    private static Stream? Open(string file)
    {
        var assembly = Assembly.GetExecutingAssembly();
        var name = assembly.GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("Frames." + file, StringComparison.OrdinalIgnoreCase));
        return name is null ? null : assembly.GetManifestResourceStream(name);
    }
}
