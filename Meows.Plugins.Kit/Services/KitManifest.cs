using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meows.Plugins.Kit.Services;

/// <summary>What a map's grid is, when it has one. Pixels per square and where the first line sits.</summary>
public sealed class GridSpec
{
    /// <summary>
    /// Off for the tables that play without one, and that is a real choice rather than a
    /// missing number: the VTT gets a gridless scene, and the size is pixels rather than squares.
    /// </summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Pixels per square. 70, 100 and 140 are what most published maps are drawn at.</summary>
    public double Size { get; set; } = 100;

    /// <summary>Where the first vertical line is, in pixels from the left edge. Usually a few pixels, rarely zero.</summary>
    public double OffsetX { get; set; }

    public double OffsetY { get; set; }
}

/// <summary>One picture in the kit: a map, a token or a handout, and what the tab knows about it.</summary>
public sealed class KitItem
{
    /// <summary>The file, relative to the kit folder, forward slashes: <c>maps/tavern.webp</c>.</summary>
    public string File { get; set; } = "";

    /// <summary>What the VTT shows. The file name is what the disk shows.</summary>
    public string Name { get; set; } = "";

    /// <summary>A line under a handout, or a note on a map. Optional.</summary>
    public string Caption { get; set; } = "";

    /// <summary>Maps only.</summary>
    public GridSpec? Grid { get; set; }

    /// <summary>Pixels of border added around the picture by a frame, so a grid origin stays honest.</summary>
    public int Padding { get; set; }

    /// <summary>Which frame was applied, by its file name, or null.</summary>
    public string? Frame { get; set; }

    /// <summary>Tokens: the disc drawn behind the portrait, by its file name, or null for none.</summary>
    public string? Background { get; set; }

    /// <summary>Tokens: friend, foe, neutral, boss. A word for the ring colour and the VTT's disposition.</summary>
    public string Side { get; set; } = "";

    public int Width { get; set; }

    public int Height { get; set; }

    [JsonIgnore]
    public string FileName => Path.GetFileName(File);
}

/// <summary>
/// One fight, or one scene of the run sheet: which map it is on, who is in it, and what the GM
/// wants to remember. The roster is text, a line per group, <c>3 Goblin</c> or <c>Goblin x3</c>,
/// matched to tokens by name when the kit is written; a line that matches no token is kept as
/// a line, because "the barkeep hides behind the counter" is part of the roster too.
/// </summary>
public sealed class Encounter
{
    public string Name { get; set; } = "";

    /// <summary>The map's file, relative to the kit, or empty for an encounter that is not on a map.</summary>
    public string Map { get; set; } = "";

    public string Roster { get; set; } = "";

    public string Notes { get; set; } = "";
}

/// <summary>
/// The kit: a title, the four lists in the order the evening reaches them, and a version so the
/// Foundry module can refuse what it does not understand rather than guess.
/// </summary>
public sealed class KitManifest
{
    public const string FileName = "kit.json";

    public const int CurrentVersion = 1;

    public int Version { get; set; } = CurrentVersion;

    public string Title { get; set; } = "";

    public string Description { get; set; } = "";

    public List<KitItem> Maps { get; set; } = [];

    public List<KitItem> Tokens { get; set; } = [];

    public List<KitItem> Handouts { get; set; } = [];

    /// <summary>Markdown files under notes/, relative paths, in order.</summary>
    public List<string> Notes { get; set; } = [];

    /// <summary>The run sheet: the fights in the order the evening reaches them.</summary>
    public List<Encounter> Encounters { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public static KitManifest Load(string folder)
    {
        var path = Path.Combine(folder, FileName);
        if (!File.Exists(path))
            return new KitManifest { Title = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) };

        try
        {
            return JsonSerializer.Deserialize<KitManifest>(File.ReadAllText(path), Json) ?? new KitManifest();
        }
        catch (Exception)
        {
            // A manifest that cannot be read is treated as absent: the folder is still a folder
            // of pictures, and the tab rebuilds what it can from those.
            return new KitManifest { Title = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar)) };
        }
    }

    public void Save(string folder)
    {
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, FileName), JsonSerializer.Serialize(this, Json));
    }

    public IEnumerable<KitItem> All => Maps.Concat(Tokens).Concat(Handouts);

    /// <summary>
    /// Brings the manifest in line with the folder: pictures on disk with no entry get one, entries
    /// whose file is gone are dropped. The folder is the truth; the manifest is what is known
    /// about it.
    /// </summary>
    public bool Reconcile(string folder)
    {
        var changed = false;
        foreach (var (list, sub) in new[] { (Maps, "maps"), (Tokens, "tokens"), (Handouts, "handouts") })
        {
            var dir = Path.Combine(folder, sub);
            var onDisk = Directory.Exists(dir)
                ? Directory.EnumerateFiles(dir).Where(IsPicture).Select(f => $"{sub}/{Path.GetFileName(f)}").ToList()
                : [];

            var gone = list.Where(i => !onDisk.Contains(i.File, StringComparer.OrdinalIgnoreCase)).ToList();
            foreach (var item in gone)
            {
                list.Remove(item);
                changed = true;
            }

            foreach (var file in onDisk.Where(f => list.All(i => !string.Equals(i.File, f, StringComparison.OrdinalIgnoreCase))))
            {
                list.Add(new KitItem
                {
                    File = file,
                    Name = Path.GetFileNameWithoutExtension(file),
                    Grid = sub == "maps" ? new GridSpec() : null,
                });
                changed = true;
            }
        }

        var notesDir = Path.Combine(folder, "notes");
        var notes = Directory.Exists(notesDir)
            ? Directory.EnumerateFiles(notesDir, "*.md").Select(f => "notes/" + Path.GetFileName(f)).OrderBy(n => n, StringComparer.OrdinalIgnoreCase).ToList()
            : [];
        if (!notes.SequenceEqual(Notes, StringComparer.OrdinalIgnoreCase))
        {
            Notes = notes;
            changed = true;
        }

        return changed;
    }

    public static bool IsPicture(string path) => Path.GetExtension(path).ToLowerInvariant() is ".png" or ".jpg" or ".jpeg" or ".webp";
}
