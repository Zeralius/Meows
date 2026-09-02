using System.Text;
using System.Text.RegularExpressions;

namespace Meows.Plugins.Kibble.Services;

/// <summary>Where the name for a new comic comes from.</summary>
public enum ComicNaming
{
    /// <summary>The folder you opened. Clashes get a number, so set_2, set_3.</summary>
    Folder,

    /// <summary>The words the picked files actually share.</summary>
    Weighted,

    /// <summary>The folder with a short random tag, so nothing ever collides.</summary>
    RandomTag,
}

/// <summary>
/// Naming a comic from the files going into it. Worth doing properly because the archive name is
/// the only label the set ever gets, and "set_7.cbz" tells you nothing a month later.
/// </summary>
public static partial class ComicName
{
    /// <summary>A word has to turn up in at least this many of the picked files to count.</summary>
    private const int MinFiles = 2;

    /// <summary>More than a few words stops being a name and starts being a sentence.</summary>
    private const int MaxWords = 4;

    private const int MaxLength = 60;

    [GeneratedRegex(@"[^a-z0-9]+")]
    private static partial Regex Separators();

    /// <summary>
    /// The words the picked files have in common, commonest first. Digits are dropped, since
    /// page numbers and indices are exactly what differs between the files rather than what
    /// they share. Falls back to <paramref name="fallback"/> when nothing is shared, which is
    /// what happens with hash named downloads.
    /// </summary>
    public static string Weighted(IEnumerable<string> fileNames, string fallback)
    {
        var files = fileNames.ToList();
        if (files.Count == 0)
            return Clean(fallback);

        // Counted once per file, so one file called foxy_foxy_foxy cannot outvote the others.
        var counts = new Dictionary<string, int>();
        var firstSeen = new Dictionary<string, int>();

        foreach (var name in files)
        {
            var words = Words(name);
            var seenHere = new HashSet<string>();
            for (var i = 0; i < words.Count; i++)
            {
                var word = words[i];
                if (!seenHere.Add(word))
                    continue;

                counts[word] = counts.GetValueOrDefault(word) + 1;
                if (!firstSeen.ContainsKey(word))
                    firstSeen[word] = i;
            }
        }

        var shared = counts
            .Where(pair => pair.Value >= Math.Min(MinFiles, files.Count))
            .OrderByDescending(pair => pair.Value)
            .ThenBy(pair => firstSeen[pair.Key])
            .ThenBy(pair => pair.Key, StringComparer.Ordinal)
            .Take(MaxWords)
            .Select(pair => pair.Key)
            .ToList();

        return shared.Count == 0 ? Clean(fallback) : Clean(string.Join(" ", shared));
    }

    /// <summary>The folder name with a short random tag, so two picks never collide.</summary>
    public static string WithRandomTag(string folderName)
    {
        // No vowels, so the tag cannot accidentally spell anything.
        const string alphabet = "bcdfghjkmnpqrstvwxz23456789";
        var tag = new StringBuilder(4);
        for (var i = 0; i < 4; i++)
            tag.Append(alphabet[Random.Shared.Next(alphabet.Length)]);

        var stem = Clean(folderName);
        return stem.Length == 0 ? tag.ToString() : $"{stem}-{tag}";
    }

    /// <summary>Lowercased words, with pure numbers left out.</summary>
    private static List<string> Words(string fileName) =>
        Separators()
            .Split(Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant())
            .Where(word => word.Length > 1)
            .Where(word => !word.All(char.IsDigit))
            .ToList();

    private static string Clean(string name)
    {
        var trimmed = string.Concat((name ?? "").Split(Path.GetInvalidFileNameChars())).Trim();
        return trimmed.Length <= MaxLength ? trimmed : trimmed[..MaxLength].Trim();
    }
}
