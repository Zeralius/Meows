using System.Text;
using System.Text.Json;
using Meows.Media;
using SkiaSharp;

namespace Meows.Plugins.Kit.Services;

/// <summary>What an export wrote, and where.</summary>
public sealed record ExportReport(string Folder, int Files, IReadOnlyList<string> Notes);

/// <summary>
/// The kit, written the way each VTT wants it. Foundry gets the folder as it is plus the
/// numbers the module needs worked out; Roll20 gets pictures sized to its 70-pixel units and a
/// sheet saying what to do with each, because there is no way in.
/// </summary>
public static class Exporter
{
    /// <summary>Roll20 measures a page in these. A map sized to whole units snaps to the grid on upload.</summary>
    public const int Roll20Unit = 70;

    /// <summary>
    /// The kit for the Foundry module: every file, and kit.json with the derived numbers in it,
    /// so the module never has to guess at a grid. The folder name is the kit's slug; the module
    /// takes a whole folder from Foundry's file picker or from wherever it was dropped.
    /// </summary>
    public static ExportReport ForFoundry(string kitFolder, KitManifest manifest, string targetRoot)
    {
        var target = Path.Combine(targetRoot, Slug(manifest.Title));
        Directory.CreateDirectory(target);
        var files = 0;

        foreach (var item in manifest.All)
        {
            var source = Path.Combine(kitFolder, item.File.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source))
                continue;
            var destination = Path.Combine(target, item.File.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            files++;
        }

        foreach (var note in manifest.Notes)
        {
            var source = Path.Combine(kitFolder, note.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source))
                continue;
            var destination = Path.Combine(target, note.Replace('/', Path.DirectorySeparatorChar));
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
            File.Copy(source, destination, overwrite: true);
            files++;
        }

        // The manifest with the numbers the module wants: pixels, squares, and a gridless flag
        // that is a decision rather than a missing value.
        var scenes = manifest.Maps.Select(m =>
        {
            var grid = m.Grid ?? new GridSpec { Enabled = false };
            var (columns, rows) = grid.Enabled ? Pictures.Squares(m.Width - 2 * m.Padding, m.Height - 2 * m.Padding, grid) : (0, 0);
            return new
            {
                name = m.Name,
                file = m.File,
                caption = m.Caption,
                width = m.Width,
                height = m.Height,
                padding = m.Padding,
                gridless = !grid.Enabled,
                gridSize = grid.Enabled ? (int)Math.Round(grid.Size) : 0,
                gridOffsetX = grid.Enabled ? (int)Math.Round(grid.OffsetX) + m.Padding : 0,
                gridOffsetY = grid.Enabled ? (int)Math.Round(grid.OffsetY) + m.Padding : 0,
                columns,
                rows,
            };
        });

        var json = JsonSerializer.Serialize(new
        {
            meowsKit = KitManifest.CurrentVersion,
            title = manifest.Title,
            description = manifest.Description,
            scenes,
            tokens = manifest.Tokens.Select(t => new { name = t.Name, file = t.File, side = t.Side, width = t.Width, height = t.Height }),
            handouts = manifest.Handouts.Select(h => new { name = h.Name, file = h.File, caption = h.Caption }),
            notes = manifest.Notes,
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(target, KitManifest.FileName), json);
        files++;

        return new ExportReport(target, files, ["foundry"]);
    }

    /// <summary>
    /// Roll20 has no way in, so this is the files spelled Roll20's way and a sheet: maps scaled
    /// to whole 70-pixel units so the page grid lines up on upload, JPEG under the per-file
    /// limit, numbered in the evening's order; tokens as PNG; handouts as JPEG; ROLL20.md
    /// saying which page gets which size and which file.
    /// </summary>
    public static ExportReport ForRoll20(string kitFolder, KitManifest manifest, string targetRoot, long maxBytes)
    {
        var target = Path.Combine(targetRoot, Slug(manifest.Title) + " (Roll20)");
        Directory.CreateDirectory(target);
        var files = 0;
        var notes = new List<string>();
        var sheet = new StringBuilder();
        sheet.AppendLine($"# {manifest.Title} — Roll20");
        sheet.AppendLine();
        sheet.AppendLine("Roll20 has no way in from outside, so this folder is the files sized and named its way,");
        sheet.AppendLine("and this sheet is what to do with each. Make a folder in the Art Library by hand first.");
        sheet.AppendLine();
        sheet.AppendLine("## Pages (one per map, in this order)");
        sheet.AppendLine();

        var index = 1;
        foreach (var map in manifest.Maps)
        {
            var source = Path.Combine(kitFolder, map.File.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(source))
                continue;

            using var bitmap = Preparer.Decode(File.ReadAllBytes(source));
            if (bitmap is null)
            {
                notes.Add($"{map.FileName} could not be read");
                continue;
            }

            var grid = map.Grid ?? new GridSpec { Enabled = false };
            var name = $"{index:00} {Safe(map.Name)}.jpg";
            int targetW, targetH;
            string sizeLine;

            if (grid.Enabled)
            {
                // Whole units: the picture is resampled so one of its squares is exactly 70 px,
                // and the page is set to that many units. The border padding is scaled with it.
                var (columns, rows) = Pictures.Squares(bitmap.Width - 2 * map.Padding, bitmap.Height - 2 * map.Padding, grid);
                var scale = Roll20Unit / grid.Size;
                targetW = (int)Math.Round(bitmap.Width * scale);
                targetH = (int)Math.Round(bitmap.Height * scale);
                var padUnits = map.Padding * scale / Roll20Unit;
                sizeLine = $"page size **{columns + 2 * padUnits:0.##} × {rows + 2 * padUnits:0.##} units**, grid **on**, 70 px";
                if (map.Padding > 0)
                    sizeLine += $" (the frame adds {padUnits:0.##} units each side; grid offset {padUnits:0.##})";
            }
            else
            {
                targetW = bitmap.Width;
                targetH = bitmap.Height;
                sizeLine = $"page size **{bitmap.Width / (double)Roll20Unit:0.##} × {bitmap.Height / (double)Roll20Unit:0.##} units**, grid **off**";
            }

            using var scaled = targetW == bitmap.Width && targetH == bitmap.Height
                ? bitmap.Copy()
                : bitmap.Resize(new SKImageInfo(targetW, targetH), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

            var bytes = UnderLimit(scaled, maxBytes, out var quality);
            File.WriteAllBytes(Path.Combine(target, name), bytes);
            files++;

            sheet.AppendLine($"{index}. **{map.Name}** — `{name}`, {sizeLine}, {bytes.Length / 1_000_000d:0.#} MB" +
                             (quality < 90 ? $" (JPEG quality {quality} to fit under {maxBytes / 1_000_000} MB)" : "") + ".");
            sheet.AppendLine($"   Create the page, set its size, drop the file on the **Map & Background** layer, align to the top-left corner.");
            if (map.Caption.Length > 0)
                sheet.AppendLine($"   Note: {map.Caption}");
            index++;
        }

        if (manifest.Tokens.Count > 0)
        {
            sheet.AppendLine();
            sheet.AppendLine("## Tokens");
            sheet.AppendLine();
            foreach (var token in manifest.Tokens)
            {
                var source = Path.Combine(kitFolder, token.File.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(source))
                    continue;
                var name = $"token {Safe(token.Name)}.png";
                File.Copy(source, Path.Combine(target, name), overwrite: true);
                files++;
                sheet.AppendLine($"- **{token.Name}** — `{name}`" + (token.Side.Length > 0 ? $", {token.Side}" : "") + ". Upload to the library, drag onto the token layer.");
            }
        }

        if (manifest.Handouts.Count > 0)
        {
            sheet.AppendLine();
            sheet.AppendLine("## Handouts");
            sheet.AppendLine();
            foreach (var handout in manifest.Handouts)
            {
                var source = Path.Combine(kitFolder, handout.File.Replace('/', Path.DirectorySeparatorChar));
                if (!File.Exists(source))
                    continue;
                using var bitmap = Preparer.Decode(File.ReadAllBytes(source));
                if (bitmap is null)
                    continue;
                var name = $"handout {Safe(handout.Name)}.jpg";
                File.WriteAllBytes(Path.Combine(target, name), UnderLimit(bitmap, maxBytes, out _));
                files++;
                sheet.AppendLine($"- **{handout.Name}** — `{name}`. Journal → new handout, this picture as its image" +
                                 (handout.Caption.Length > 0 ? $", caption: {handout.Caption}" : "") + ".");
            }
        }

        File.WriteAllText(Path.Combine(target, "ROLL20.md"), sheet.ToString());
        files++;
        return new ExportReport(target, files, notes);
    }

    /// <summary>JPEG at the best quality that fits under the limit, stepping down until it does.</summary>
    private static byte[] UnderLimit(SKBitmap bitmap, long maxBytes, out int quality)
    {
        foreach (var q in new[] { 92, 85, 78, 70, 60, 50 })
        {
            var bytes = Preparer.Encode(bitmap, ImageFormat.Jpeg, q);
            if (bytes.LongLength <= maxBytes || q == 50)
            {
                quality = q;
                return bytes;
            }
        }
        quality = 50;
        return Preparer.Encode(bitmap, ImageFormat.Jpeg, 50);
    }

    public static string Slug(string title)
    {
        var cleaned = string.Concat(title.Trim().Split(Path.GetInvalidFileNameChars())).Trim();
        return cleaned.Length == 0 ? "kit" : cleaned;
    }

    private static string Safe(string name) => Slug(name);
}
