using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Meows.Plugins.Rehome.Services;

/// <summary>
/// The program list as files, written to the drive that survives: one to read, one for the
/// plugin, and one script per manager. The file comes before the tab, because the tab will
/// not exist next week.
/// </summary>
public static class ProgramLists
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Converters = { new JsonStringEnumConverter(JsonNamingPolicy.CamelCase) },
    };

    public sealed record Written(string Folder, IReadOnlyList<string> Files);

    public static Written Write(string folder, IReadOnlyList<ProgramMatch> all, ManagerReport managers, DateTime when, IReadOnlyList<string>? wipedDrives = null)
    {
        Directory.CreateDirectory(folder);
        var files = new List<string>();

        // What the wipe takes is the list; what sits on another drive keeps its files and is
        // listed apart, because a launcher finds those again and the rest only need the entry back.
        var wiped = wipedDrives ?? [];
        var matches = wipedDrives is null ? all : all.Where(m => m.Program.OnWipedDrive(wiped)).ToList();
        var elsewhere = wipedDrives is null ? [] : all.Where(m => !m.Program.OnWipedDrive(wiped)).ToList();

        var winget = matches.Where(m => m.Manager == Manager.Winget).ToList();
        var choco = matches.Where(m => m.Manager == Manager.Chocolatey).ToList();
        var scoop = matches.Where(m => m.Manager == Manager.Scoop).ToList();
        var launchers = matches.Where(m => m.Manager == Manager.Launcher).ToList();
        var byHand = matches.Where(m => m.ByHand).ToList();

        // programs.json: the plugin's own, everything the registry said and what was matched.
        var json = Path.Combine(folder, "programs.json");
        File.WriteAllText(json, JsonSerializer.Serialize(new
        {
            writtenAt = when,
            machine = Environment.MachineName,
            user = Environment.UserName,
            wipedDrives = wiped,
            programs = all.Select(m => new
            {
                m.Program.Name,
                onWipedDrive = m.Program.OnWipedDrive(wiped),
                m.Program.Publisher,
                m.Program.Version,
                installedOn = m.Program.InstalledOn?.ToString("yyyy-MM-dd"),
                m.Program.Location,
                m.Program.Url,
                m.Program.PerUser,
                manager = m.Manager,
                m.Id,
                m.Confidence,
            }),
        }, Json));
        files.Add(json);

        // programs.md: the one to read in six months.
        var md = new StringBuilder();
        md.AppendLine($"# Programs on {Environment.MachineName}");
        md.AppendLine();
        md.AppendLine($"Written {when:yyyy-MM-dd HH:mm} by Rehome for {Environment.UserName}. {matches.Count} programs the wipe takes: " +
                      $"{winget.Count} winget, {choco.Count} Chocolatey, {scoop.Count} Scoop, {launchers.Count} through a game launcher, {byHand.Count} by hand." +
                      (elsewhere.Count == 0 ? "" : $" {elsewhere.Count} more sit on a drive not being wiped and keep their files."));
        md.AppendLine();
        Section(md, "winget", winget, "`winget install --id <id>`, or `winget import` with the file beside this one");
        Section(md, "Chocolatey", choco, "`choco install <id> -y`, after Chocolatey itself");
        Section(md, "Scoop", scoop, "`scoop install <id>`, after Scoop itself");
        Section(md, "By hand", byHand, "no manager knows these; the link is the publisher's, from the registry");
        Section(md, "Through a launcher", launchers, "sign in to the launcher named in the Id column and install from its library; nothing to download by hand");
        if (elsewhere.Count > 0)
            Section(md, "On a drive not being wiped", elsewhere, "the files stay; only the registry entry goes. A launcher finds its games again once the library folder is added back; anything else may want its installer run over the top");
        var mdPath = Path.Combine(folder, "programs.md");
        File.WriteAllText(mdPath, md.ToString());
        files.Add(mdPath);

        // winget-import.json: winget's own export where it gave one, else built from the matches.
        if (winget.Count > 0 || managers.WingetExportJson is not null)
        {
            var import = Path.Combine(folder, "winget-import.json");
            File.WriteAllText(import, managers.WingetExportJson ?? BuildWingetImport(winget));
            files.Add(import);
        }

        if (choco.Count > 0)
        {
            var config = new StringBuilder();
            config.AppendLine("<?xml version=\"1.0\" encoding=\"utf-8\"?>");
            config.AppendLine("<packages>");
            foreach (var m in choco)
                config.AppendLine($"  <package id=\"{Escape(m.Id!)}\" />");
            config.AppendLine("</packages>");
            var path = Path.Combine(folder, "chocolatey-packages.config");
            File.WriteAllText(path, config.ToString());
            files.Add(path);
        }

        if (scoop.Count > 0)
        {
            var path = Path.Combine(folder, "scoop-apps.txt");
            File.WriteAllLines(path, scoop.Select(m => m.Id!));
            files.Add(path);
        }

        var script = Path.Combine(folder, "install.ps1");
        File.WriteAllText(script, BuildScript(winget, choco, scoop, byHand, launchers, elsewhere, when));
        files.Add(script);

        return new Written(folder, files);
    }

    /// <summary>
    /// The short list: the programs the person wants back, ticked by hand, as one file to read and
    /// one to run. The manager's line where a manager knows the program, a search line as a hint
    /// where none does, so the by-hand rows are still one paste away from being asked about.
    /// </summary>
    public static IReadOnlyList<string> WriteToGet(string folder, IReadOnlyList<ProgramMatch> wanted, DateTime when)
    {
        Directory.CreateDirectory(folder);
        var md = new StringBuilder();
        md.AppendLine($"# To get again on {Environment.MachineName}");
        md.AppendLine();
        md.AppendLine($"{wanted.Count} programs ticked by hand in Rehome, {when:yyyy-MM-dd HH:mm}. The script beside this file installs what a manager knows and lists the rest.");
        md.AppendLine();
        md.AppendLine("| Program | Version | Publisher | How | Link |");
        md.AppendLine("|---|---|---|---|---|");
        foreach (var m in wanted.OrderBy(m => m.Program.Name, StringComparer.OrdinalIgnoreCase))
            md.AppendLine($"| {Cell(m.Program.Name)} | {Cell(m.Program.Version)} | {Cell(m.Program.Publisher)} | {Cell(How(m))} | {Cell(m.Program.Url)} |");
        var mdPath = Path.Combine(folder, "to-get.md");
        File.WriteAllText(mdPath, md.ToString());

        var s = new StringBuilder();
        s.AppendLine($"# Rehome, to get again: {wanted.Count} programs ticked by hand, {when:yyyy-MM-dd HH:mm}.");
        s.AppendLine("# Run in an elevated PowerShell on the new Windows. Each line stands on its own.");
        s.AppendLine();
        foreach (var m in wanted.OrderBy(m => m.Manager).ThenBy(m => m.Program.Name, StringComparer.OrdinalIgnoreCase))
        {
            switch (m.Manager)
            {
                case Manager.Winget:
                    s.AppendLine($"winget install --id {m.Id} --exact --accept-package-agreements --accept-source-agreements   # {m.Program.Name}");
                    break;
                case Manager.Chocolatey:
                    s.AppendLine($"choco install {m.Id} -y   # {m.Program.Name}");
                    break;
                case Manager.Scoop:
                    s.AppendLine($"scoop install {m.Id}   # {m.Program.Name}");
                    break;
                case Manager.Launcher:
                    s.AppendLine($"# {m.Program.Name}: sign in to {m.Id} and install from the library");
                    break;
                default:
                    var link = string.IsNullOrWhiteSpace(m.Program.Url) ? "" : $"  {m.Program.Url}";
                    s.AppendLine($"# {m.Program.Name}{(m.Program.Publisher is null ? "" : $" ({m.Program.Publisher})")}: by hand.{link}");
                    s.AppendLine($"#   winget search --name \"{m.Program.BareName.Replace("\"", "")}\"   # in case winget knows it after all; or choco search");
                    break;
            }
        }
        if (wanted.Any(m => m.Manager == Manager.Chocolatey))
        {
            s.Insert(0, "if (-not (Get-Command choco -ErrorAction SilentlyContinue)) { Set-ExecutionPolicy Bypass -Scope Process -Force; [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072; Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1')) }\n\n");
            s.Insert(0, "# Chocolatey itself first; a fresh machine has not got it.\n");
        }
        var scriptPath = Path.Combine(folder, "to-get.ps1");
        File.WriteAllText(scriptPath, s.ToString());
        return [mdPath, scriptPath];
    }

    private static string How(ProgramMatch m) => m.Manager switch
    {
        Manager.Winget => $"winget install --id {m.Id}",
        Manager.Chocolatey => $"choco install {m.Id}",
        Manager.Scoop => $"scoop install {m.Id}",
        Manager.Launcher => $"{m.Id}: sign in",
        _ => "by hand",
    };

    private static void Section(StringBuilder md, string title, IReadOnlyList<ProgramMatch> rows, string how)
    {
        md.AppendLine($"## {title} ({rows.Count})");
        md.AppendLine();
        md.AppendLine(how);
        md.AppendLine();
        if (rows.Count == 0)
        {
            md.AppendLine("*none*");
            md.AppendLine();
            return;
        }
        md.AppendLine("| Program | Version | Publisher | Id | Sure? | Installed | Link |");
        md.AppendLine("|---|---|---|---|---|---|---|");
        foreach (var m in rows)
        {
            var sure = m.Confidence switch
            {
                MatchConfidence.Exact => "yes",
                MatchConfidence.Likely => "likely",
                _ => "",
            };
            md.AppendLine($"| {Cell(m.Program.Name)} | {Cell(m.Program.Version)} | {Cell(m.Program.Publisher)} | {Cell(m.Id is null ? "" : $"`{m.Id}`")} | {sure} | {m.Program.InstalledOn?.ToString("yyyy-MM-dd")} | {Cell(m.Program.Url)} |");
        }
        md.AppendLine();
    }

    private static string Cell(string? text) => (text ?? "").Replace("|", "\\|").Replace("\r", "").Replace("\n", " ");

    private static string Escape(string text) => text.Replace("&", "&amp;").Replace("\"", "&quot;").Replace("<", "&lt;").Replace(">", "&gt;");

    /// <summary>The shape `winget export` writes, so `winget import` takes it.</summary>
    private static string BuildWingetImport(IReadOnlyList<ProgramMatch> winget) => JsonSerializer.Serialize(new
    {
        Schema = "https://aka.ms/winget-packages.schema.2.0.json",
        Sources = new[]
        {
            new
            {
                Packages = winget.Select(m => new { PackageIdentifier = m.Id }).ToArray(),
                SourceDetails = new
                {
                    Argument = "https://cdn.winget.microsoft.com/cache",
                    Identifier = "Microsoft.Winget.Source_8wekyb3d8bbwe",
                    Name = "winget",
                    Type = "Microsoft.PreIndexed.Package",
                },
            },
        },
    }, new JsonSerializerOptions { WriteIndented = true }).Replace("\"Schema\"", "\"$schema\"");

    private static string BuildScript(IReadOnlyList<ProgramMatch> winget, IReadOnlyList<ProgramMatch> choco, IReadOnlyList<ProgramMatch> scoop, IReadOnlyList<ProgramMatch> byHand, IReadOnlyList<ProgramMatch> launchers, IReadOnlyList<ProgramMatch> elsewhere, DateTime when)
    {
        var s = new StringBuilder();
        s.AppendLine($"# Rehome: {Environment.MachineName}, {Environment.UserName}, written {when:yyyy-MM-dd HH:mm}.");
        s.AppendLine("# Run in an elevated PowerShell on the new Windows, from the folder this file is in.");
        s.AppendLine("# Every block stands on its own; comment out what you do not want. The managers install");
        s.AppendLine("# the latest version, not the one that was here.");
        s.AppendLine();
        s.AppendLine("$here = Split-Path -Parent $MyInvocation.MyCommand.Path");
        s.AppendLine();

        s.AppendLine($"# ---- winget: {winget.Count} programs ----");
        if (winget.Count > 0)
        {
            s.AppendLine("# winget ships with Windows 11 and recent Windows 10; if it is missing, install \"App Installer\" from the Store.");
            s.AppendLine("winget import -i \"$here\\winget-import.json\" --accept-package-agreements --accept-source-agreements --ignore-unavailable");
        }
        s.AppendLine();

        s.AppendLine($"# ---- Chocolatey: {choco.Count} programs ----");
        if (choco.Count > 0)
        {
            s.AppendLine("# Chocolatey itself first; a fresh machine has not got it.");
            s.AppendLine("if (-not (Get-Command choco -ErrorAction SilentlyContinue)) {");
            s.AppendLine("    Set-ExecutionPolicy Bypass -Scope Process -Force");
            s.AppendLine("    [System.Net.ServicePointManager]::SecurityProtocol = [System.Net.ServicePointManager]::SecurityProtocol -bor 3072");
            s.AppendLine("    Invoke-Expression ((New-Object System.Net.WebClient).DownloadString('https://community.chocolatey.org/install.ps1'))");
            s.AppendLine("}");
            s.AppendLine("choco install \"$here\\chocolatey-packages.config\" -y");
        }
        s.AppendLine();

        s.AppendLine($"# ---- Scoop: {scoop.Count} programs ----");
        if (scoop.Count > 0)
        {
            s.AppendLine("# Scoop itself first, and it wants a non-elevated shell for that step.");
            s.AppendLine("if (-not (Get-Command scoop -ErrorAction SilentlyContinue)) {");
            s.AppendLine("    Set-ExecutionPolicy RemoteSigned -Scope CurrentUser -Force");
            s.AppendLine("    Invoke-RestMethod get.scoop.sh | Invoke-Expression");
            s.AppendLine("}");
            s.AppendLine("Get-Content \"$here\\scoop-apps.txt\" | ForEach-Object { scoop install $_ }");
        }
        s.AppendLine();

        s.AppendLine($"# ---- By hand: {byHand.Count} programs no manager knows ----");
        s.AppendLine("# Twenty minutes that are still twenty minutes. Name, version, publisher, and the publisher's link where the registry had one.");
        foreach (var m in byHand)
        {
            var bits = new[] { m.Program.Name, m.Program.Version, m.Program.Publisher, m.Program.Url }.Where(b => !string.IsNullOrWhiteSpace(b));
            s.AppendLine("#   " + string.Join("  |  ", bits));
        }
        s.AppendLine();

        s.AppendLine($"# ---- Through a launcher: {launchers.Count} games ----");
        s.AppendLine("# Sign in to the launcher and install from its library. Listed so the library can be checked against what was here.");
        foreach (var group in launchers.GroupBy(m => m.Id).OrderBy(g => g.Key))
        {
            s.AppendLine($"#   {group.Key} ({group.Count()}):");
            foreach (var m in group.OrderBy(m => m.Program.Name, StringComparer.OrdinalIgnoreCase))
                s.AppendLine($"#     {m.Program.Name}");
        }

        if (elsewhere.Count > 0)
        {
            s.AppendLine();
            s.AppendLine($"# ---- On a drive not being wiped: {elsewhere.Count} programs ----");
            s.AppendLine("# The files stay; only the registry entry goes. Steam and the others find their games once the");
            s.AppendLine("# library folder is added back; anything else may want its installer run over the top.");
            foreach (var m in elsewhere.OrderBy(m => m.Program.Name, StringComparer.OrdinalIgnoreCase))
                s.AppendLine($"#   {m.Program.Name}  |  {m.Program.Location}");
        }
        return s.ToString();
    }
}
