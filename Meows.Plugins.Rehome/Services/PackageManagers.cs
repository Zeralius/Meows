using System.Text;
using System.Text.Json;

namespace Meows.Plugins.Rehome.Services;

public enum Manager { None, Winget, Chocolatey, Scoop, Launcher }

public enum MatchConfidence { None, Likely, Exact }

/// <summary>One package a manager says is installed: the id it would reinstall it by, and the name it shows.</summary>
public sealed record ManagedPackage(Manager Manager, string Id, string Name, string? Version);

/// <summary>What the managers on this machine know. A manager that is not installed has an empty list and a reason.</summary>
public sealed record ManagerReport(
    IReadOnlyList<ManagedPackage> Winget, string? WingetReason,
    IReadOnlyList<ManagedPackage> Chocolatey, string? ChocolateyReason,
    IReadOnlyList<ManagedPackage> Scoop, string? ScoopReason,
    string? WingetExportJson)
{
    public static readonly ManagerReport Empty = new([], null, [], null, [], null, null);

    public bool HasWinget => WingetReason is null;
    public bool HasChocolatey => ChocolateyReason is null;
    public bool HasScoop => ScoopReason is null;
}

/// <summary>A program and the way to get it back: which manager, by which id, and how sure the match is.</summary>
public sealed record ProgramMatch(InstalledProgram Program, Manager Manager, string? Id, MatchConfidence Confidence)
{
    public bool ByHand => Manager == Manager.None;
}

/// <summary>
/// Asks winget, Chocolatey and Scoop what they have, and matches the registry list against it.
/// winget's own list is the registry list with ids beside it, so an exact name match there is
/// as sure as it gets; Chocolatey and Scoop name packages in their own slugs, so those are
/// matched by a flattened name and never called more than likely.
/// </summary>
public static class PackageManagers
{
    public static async Task<ManagerReport> ProbeAsync(Action<string> report, CancellationToken token)
    {
        report("winget");
        var (winget, wingetReason, export) = await ProbeWingetAsync(token);
        token.ThrowIfCancellationRequested();
        report("Chocolatey");
        var (choco, chocoReason) = await ProbeChocolateyAsync(token);
        token.ThrowIfCancellationRequested();
        report("Scoop");
        var (scoop, scoopReason) = await ProbeScoopAsync(token);
        return new ManagerReport(winget, wingetReason, choco, chocoReason, scoop, scoopReason, export);
    }

    // ---- winget ----

    private static async Task<(IReadOnlyList<ManagedPackage>, string?, string?)> ProbeWingetAsync(CancellationToken token)
    {
        if (!Run.OnPath("winget"))
            return ([], "not installed", null);

        var list = await Run.CaptureAsync("winget", ["list", "--accept-source-agreements", "--disable-interactivity"], token, TimeSpan.FromMinutes(5));
        if (!list.Started)
            return ([], list.FailureReason ?? "would not start", null);
        var packages = ParseWingetTable(list.Output);

        // The real import file, from winget itself, so the way back is winget's own and not a guess.
        string? export = null;
        var file = Path.Combine(Path.GetTempPath(), "rehome-winget-" + Guid.NewGuid().ToString("N")[..8] + ".json");
        try
        {
            var exported = await Run.CaptureAsync("winget", ["export", "-o", file, "--accept-source-agreements", "--disable-interactivity"], token, TimeSpan.FromMinutes(5));
            if (exported.Started && File.Exists(file))
                export = await File.ReadAllTextAsync(file, token);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // The list is still worth having without it.
        }
        finally
        {
            try { File.Delete(file); } catch (Exception) { }
        }

        return (packages, null, export);
    }

    /// <summary>
    /// winget prints a table meant for eyes, in the language of the machine: a header line, a
    /// rule of dashes, then rows cut at the header's column starts. The columns are found by
    /// where the header's words begin, not by what they say, since "Id" is "ID" here and
    /// "Available" is "Verfügbar" on a German machine; and the fourth column is only there when
    /// something has an update, so the source is the last column, whatever the count.
    /// </summary>
    public static IReadOnlyList<ManagedPackage> ParseWingetTable(IReadOnlyList<string> lines)
    {
        var headerAt = -1;
        for (var i = 0; i < lines.Count - 1; i++)
        {
            if (lines[i].Length > 0 && !char.IsWhiteSpace(lines[i][0]) && lines[i + 1].TrimStart().StartsWith("---", StringComparison.Ordinal))
            {
                headerAt = i;
                break;
            }
        }
        if (headerAt < 0)
            return [];

        var starts = ColumnStarts(lines[headerAt]);
        if (starts.Count < 4)
            return [];
        var idAt = starts[1];
        var versionAt = starts[2];
        var afterVersion = starts[3];
        var sourceAt = starts[^1];

        var found = new List<ManagedPackage>();
        foreach (var raw in lines.Skip(headerAt + 2))
        {
            var line = raw.TrimEnd();
            if (line.Length <= versionAt)
                continue;
            var name = Cut(line, 0, idAt);
            var id = Cut(line, idAt, versionAt);
            var version = Cut(line, versionAt, afterVersion);
            var source = Cut(line, sourceAt, line.Length);
            if (name.Length == 0 || id.Length == 0)
                continue;
            // Rows with no source are the registry's own, not something winget can put back.
            if (!string.Equals(source, "winget", StringComparison.OrdinalIgnoreCase))
                continue;
            found.Add(new ManagedPackage(Manager.Winget, id, name, version.Length == 0 ? null : version));
        }
        return found;

        static string Cut(string line, int from, int to)
        {
            if (from >= line.Length)
                return "";
            var end = Math.Min(to, line.Length);
            return end <= from ? "" : line[from..end].Trim();
        }
    }

    /// <summary>Where each word of the header starts: a column begins where a word follows at least two spaces.</summary>
    private static List<int> ColumnStarts(string header)
    {
        var starts = new List<int>();
        var inWord = false;
        var gap = 2;
        for (var i = 0; i < header.Length; i++)
        {
            if (char.IsWhiteSpace(header[i]))
            {
                gap++;
                inWord = false;
                continue;
            }
            if (!inWord && (gap >= 2 || starts.Count == 0))
                starts.Add(i);
            inWord = true;
            gap = 0;
        }
        return starts;
    }

    // ---- Chocolatey ----

    private static async Task<(IReadOnlyList<ManagedPackage>, string?)> ProbeChocolateyAsync(CancellationToken token)
    {
        if (!Run.OnPath("choco"))
            return ([], "not installed");

        var version = await Run.CaptureAsync("choco", ["--version"], token);
        var major = version.Output.Select(l => l.Trim()).FirstOrDefault(l => l.Length > 0 && char.IsDigit(l[0]))?.Split('.')[0];
        // 1.x lists the remote unless told local; 2.x lists local and rejects the flag.
        var arguments = major == "1" || major == "0"
            ? new[] { "list", "--local-only", "--limit-output" }
            : new[] { "list", "--limit-output" };
        var list = await Run.CaptureAsync("choco", arguments, token);
        if (!list.Started)
            return ([], list.FailureReason ?? "would not start");

        var found = new List<ManagedPackage>();
        foreach (var line in list.Output)
        {
            var parts = line.Split('|');
            if (parts.Length < 2 || parts[0].Contains(' '))
                continue;
            var id = parts[0].Trim();
            if (id.Equals("chocolatey", StringComparison.OrdinalIgnoreCase) || id.StartsWith("chocolatey-", StringComparison.OrdinalIgnoreCase) || id.StartsWith("KB", StringComparison.Ordinal))
                continue;
            found.Add(new ManagedPackage(Manager.Chocolatey, id, id, parts[1].Trim()));
        }
        return (found, null);
    }

    // ---- Scoop ----

    private static async Task<(IReadOnlyList<ManagedPackage>, string?)> ProbeScoopAsync(CancellationToken token)
    {
        if (!Run.OnPath("scoop"))
            return ([], "not installed");

        var export = await Run.PowerShellAsync("scoop export", token);
        if (!export.Started)
            return ([], export.FailureReason ?? "would not start");
        var text = string.Join('\n', export.Output);
        var found = new List<ManagedPackage>();
        try
        {
            using var json = JsonDocument.Parse(text[text.IndexOf('{')..]);
            if (json.RootElement.TryGetProperty("apps", out var apps))
            {
                foreach (var app in apps.EnumerateArray())
                {
                    var name = app.TryGetProperty("Name", out var n) ? n.GetString() : null;
                    if (string.IsNullOrEmpty(name))
                        continue;
                    found.Add(new ManagedPackage(Manager.Scoop, name, name, app.TryGetProperty("Version", out var v) ? v.GetString() : null));
                }
            }
        }
        catch (Exception)
        {
            return ([], "the export could not be read");
        }
        return (found, null);
    }

    // ---- matching ----

    public static IReadOnlyList<ProgramMatch> Match(IReadOnlyList<InstalledProgram> programs, ManagerReport managers)
    {
        var wingetByName = new Dictionary<string, ManagedPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in managers.Winget)
            wingetByName.TryAdd(p.Name, p);
        var wingetByBare = new Dictionary<string, ManagedPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in managers.Winget)
            wingetByBare.TryAdd(InstalledProgram.Normalise(p.Name), p);
        var chocoBySlug = new Dictionary<string, ManagedPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in managers.Chocolatey)
        {
            chocoBySlug.TryAdd(Slug(p.Id), p);
            foreach (var suffix in new[] { ".install", ".portable", ".commandline" })
                if (p.Id.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    chocoBySlug.TryAdd(Slug(p.Id[..^suffix.Length]), p);
        }
        var scoopBySlug = new Dictionary<string, ManagedPackage>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in managers.Scoop)
            scoopBySlug.TryAdd(Slug(p.Id), p);

        var matches = new List<ProgramMatch>(programs.Count);
        foreach (var program in programs)
        {
            // A launcher's game is the launcher's to put back; no manager is asked about it.
            if (program.Launcher is { } launcher)
            {
                matches.Add(new ProgramMatch(program, Manager.Launcher, launcher, MatchConfidence.Exact));
                continue;
            }
            if (wingetByName.TryGetValue(program.Name, out var exact))
            {
                matches.Add(new ProgramMatch(program, Manager.Winget, exact.Id, MatchConfidence.Exact));
                continue;
            }
            var likely = wingetByBare.GetValueOrDefault(program.BareName) ?? Truncated(program.Name, managers.Winget);
            if (likely is not null)
            {
                matches.Add(new ProgramMatch(program, Manager.Winget, likely.Id, MatchConfidence.Likely));
                continue;
            }
            // The whole name flattened, then its first word: "VLC media player" is "vlc" to Chocolatey.
            var slug = Slug(program.BareName);
            var first = Slug(program.BareName.Split(' ', StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? "");
            if (Find(chocoBySlug, slug, first) is { } choco)
            {
                matches.Add(new ProgramMatch(program, Manager.Chocolatey, choco.Id, MatchConfidence.Likely));
                continue;
            }
            if (Find(scoopBySlug, slug, first) is { } scoop)
            {
                matches.Add(new ProgramMatch(program, Manager.Scoop, scoop.Id, MatchConfidence.Likely));
                continue;
            }
            matches.Add(new ProgramMatch(program, Manager.None, null, MatchConfidence.None));
        }
        return matches;
    }

    private static ManagedPackage? Find(Dictionary<string, ManagedPackage> bySlug, string slug, string first)
    {
        if (slug.Length >= 3 && bySlug.TryGetValue(slug, out var whole))
            return whole;
        if (first.Length >= 3 && first != slug && bySlug.TryGetValue(first, out var byFirst))
            return byFirst;
        return null;
    }

    /// <summary>winget cuts a long name at the column and ends it with an ellipsis; the registry has the whole of it.</summary>
    private static ManagedPackage? Truncated(string name, IReadOnlyList<ManagedPackage> winget)
    {
        foreach (var p in winget)
        {
            if (p.Name.EndsWith('…') && p.Name.Length > 4 && name.StartsWith(p.Name[..^1], StringComparison.OrdinalIgnoreCase))
                return p;
        }
        return null;
    }

    /// <summary>"Notepad++" and "notepadplusplus" are the same slug; so are "7-Zip" and "7zip".</summary>
    public static string Slug(string name)
    {
        var s = new StringBuilder(name.Length);
        foreach (var c in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
                s.Append(c);
            else if (c == '+')
                s.Append("plus");
        }
        return s.ToString();
    }
}
