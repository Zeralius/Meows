using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;

namespace Meows.Kitten;

/// <summary>
/// The files a plugin is made of, written the way the ones before it were written by hand, and
/// the four places in the repository that have to know a plugin exists.
/// </summary>
public sealed class Litter(Repo repo, Options options)
{
    private string Folder => Path.Combine(repo.Root, options.Project);

    private static readonly UTF8Encoding Utf8NoBom = new(false);

    /// <summary>Why this cannot go ahead, or null when it can.</summary>
    public string? Check()
    {
        if (!Options.IsGoodName(options.Name))
            return $"'{options.Name}' is not a name Kitten can use: one PascalCase word, letters and digits, like Whiskers or Larder.";

        if (Directory.Exists(Folder))
            return $"{repo.Relative(Folder)} already exists. Kitten only ever makes new ones.";

        if (File.ReadAllText(repo.ShippedPlugins).Contains($"Plugins.{options.Name}.{options.Name}Plugin"))
            return $"{options.Name} is already on the shipped list in {repo.Relative(repo.ShippedPlugins)}.";

        if (!File.ReadAllText(repo.ShippedPlugins).Contains(Marker))
            return $"{repo.Relative(repo.ShippedPlugins)} has lost its '{Marker}' line, so Kitten cannot tell where to add the plugin.";

        if (options.Category is not ("group.everyday" or "group.disk" or "group.bot"))
            return $"'{options.Category}' is not one of the headings the Plugins tab knows: group.everyday, group.disk or group.bot.";

        return null;
    }

    private const string Marker = "// kitten: next plugin goes here";

    // ---- what gets written ----

    private IEnumerable<(string Path, string Content)> Files()
    {
        var n = options.Name;
        yield return (Path.Combine(Folder, $"{options.Project}.csproj"), Fill(Templates.Csproj));
        yield return (Path.Combine(Folder, $"{n}Plugin.cs"), Fill(Templates.Plugin));
        yield return (Path.Combine(Folder, "ViewModels", $"{n}ViewModel.cs"), Fill(Templates.ViewModel));
        yield return (Path.Combine(Folder, "Views", $"{n}View.axaml"), Fill(Templates.View));
        yield return (Path.Combine(Folder, "Views", $"{n}View.axaml.cs"), Fill(Templates.ViewCode));
        yield return (Path.Combine(Folder, "Strings", "Strings.en.json"), Strings("en"));
        yield return (Path.Combine(Folder, "Strings", "Strings.de.json"), Strings("de"));
        yield return (Path.Combine(Folder, "README.md"), Fill(Templates.Readme));
        yield return (Path.Combine(repo.Root, "Meows.Tests", $"{n}Tests.cs"), Fill(Templates.Tests));
    }

    public IEnumerable<string> FilesToWrite() => Files().Select(f => repo.Relative(f.Path));

    public IEnumerable<string> FilesToEdit() =>
    [
        repo.Relative(repo.Solution),
        repo.Relative(repo.TestsProject),
        repo.Relative(repo.ShippedPlugins),
        repo.Relative(repo.Readme),
    ];

    public IReadOnlyList<string> Write()
    {
        var written = new List<string>();
        foreach (var (path, content) in Files())
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, content.Replace("\r\n", "\n"), Utf8NoBom);
            written.Add(repo.Relative(path));
        }

        return written;
    }

    private string Fill(string template) => template
        .Replace("__Name__", options.Name)
        .Replace("__key__", options.Key)
        .Replace("__Id__", options.Id)
        .Replace("__Icon__", options.Icon)
        .Replace("__Category__", options.Category)
        .Replace("__Plain__", options.Plain)
        .Replace("__Description__", options.Description);

    /// <summary>
    /// The catalogue, sorted by key the way every other one in the repository is, so a diff
    /// stays readable once more keys are added.
    /// </summary>
    private string Strings(string language)
    {
        var de = language == "de";
        var k = options.Key;
        var entries = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            [$"{k}.description"] = de ? options.DescriptionDe : options.Description,
            [$"{k}.name.plain"] = de ? options.PlainDe : options.Plain,
            [$"{k}.title"] = options.Name,
            [$"{k}.empty"] = de
                ? "Hier ist noch nichts. Was dieses Plugin zeigt, ist noch zu schreiben."
                : "Nothing here yet. What this plugin shows is still to be written.",
            [$"{k}.refresh"] = de ? "Aktualisieren" : "Refresh",
            [$"{k}.status.ready"] = de ? "Bereit." : "Ready.",
            [$"{k}.status.refreshed"] = de ? "Aktualisiert um {0}." : "Refreshed at {0}.",
        };

        var json = JsonSerializer.Serialize(entries, new JsonSerializerOptions
        {
            WriteIndented = true,
            Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        });
        return json + "\n";
    }

    // ---- what gets edited ----

    public IReadOnlyList<string> Edit()
    {
        var edited = new List<string>();

        // The solution, through the tool that owns the format. A .sln has GUIDs and
        // configuration blocks nobody should write by hand.
        var add = Dotnet.Run(repo.Root, "sln", repo.Solution, "add", Path.Combine(Folder, $"{options.Project}.csproj"));
        if (add.ExitCode != 0)
            throw new InvalidOperationException($"dotnet sln add failed:\n{add.Output}");
        edited.Add(repo.Relative(repo.Solution));

        // The test project, which references every plugin so its assembly lands beside the tests.
        var csproj = File.ReadAllText(repo.TestsProject);
        var lastReference = csproj.LastIndexOf("<ProjectReference Include=\"..\\Meows.Plugins.", StringComparison.Ordinal);
        if (lastReference < 0)
            throw new InvalidOperationException($"{repo.Relative(repo.TestsProject)} has no plugin references to add after.");
        var lineEnd = csproj.IndexOf('\n', lastReference) + 1;
        var line = $"    <ProjectReference Include=\"..\\{options.Project}\\{options.Project}.csproj\" />\n";
        csproj = csproj.Insert(lineEnd, line);
        File.WriteAllText(repo.TestsProject, csproj, new UTF8Encoding(true));
        edited.Add(repo.Relative(repo.TestsProject));

        // The list every plugin-wide test walks.
        var shipped = File.ReadAllText(repo.ShippedPlugins);
        shipped = shipped.Replace(Marker, $"typeof(Plugins.{options.Name}.{options.Name}Plugin),\n        {Marker}");
        File.WriteAllText(repo.ShippedPlugins, shipped, Utf8NoBom);
        edited.Add(repo.Relative(repo.ShippedPlugins));

        // The table in the README. New plugins go among the general purpose ones, above the
        // four built around the bot, so the sentence under the table stays true.
        var readme = File.ReadAllText(repo.Readme);
        var row = $"| **[{options.Name}]({options.Project}/README.md)** | {options.Description.TrimEnd('.')} |\n";
        var kibble = readme.IndexOf("| **[Kibble](", StringComparison.Ordinal);
        if (kibble < 0)
            throw new InvalidOperationException("README.md has no Kibble row to put the new one above.");
        readme = readme.Insert(kibble, row);
        File.WriteAllText(repo.Readme, readme, Utf8NoBom);
        edited.Add(repo.Relative(repo.Readme));

        return edited;
    }

    public string WhatToOpenFirst() =>
        $"""

        Open first:
          {options.Project}/ViewModels/{options.Name}ViewModel.cs   the tab's state and commands; Refresh is where the real work starts
          {options.Project}/Views/{options.Name}View.axaml           the header is done, the middle says "nothing here yet"
          {options.Project}/Strings/Strings.de.json                  the German reads as the English until someone writes it
          {options.Project}/README.md                                what it does and why it fits, in the voice of the others
        Then: a line in CHANGELOG.md, the version in Meows/Meows.csproj, and IDEAS.md if it came from there.
        """;
}
