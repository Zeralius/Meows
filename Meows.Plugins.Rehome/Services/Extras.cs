namespace Meows.Plugins.Rehome.Services;

/// <summary>The things that go with the drive and are not folders, or are folders too small and too easily forgotten to leave in the list.</summary>
public enum ExtraKind { Fonts, Hosts, GitConfig, PowerShellProfile, Path, Environment, Wifi }

/// <summary>One extra as found on this machine: whether there is anything to carry, and where it is.</summary>
public sealed record ExtraItem(ExtraKind Kind, string? Source, bool Present, string Detail);

/// <summary>
/// Most of these are a file to copy; the Wi-Fi profiles are the one with a real verb both ways.
/// They land under extras\ in the Rehome folder, named for what they are, because the ones with
/// passwords in them (Wi-Fi) must never be mistaken for anything else.
/// </summary>
public static class Extras
{
    public const string Folder = "extras";

    public static IReadOnlyList<ExtraItem> Gather()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var items = new List<ExtraItem>();

        var fonts = Path.Combine(local, "Microsoft", "Windows", "Fonts");
        var fontCount = CountFiles(fonts);
        items.Add(new ExtraItem(ExtraKind.Fonts, fonts, fontCount > 0, fontCount > 0 ? $"{fontCount} files" : ""));

        var hosts = Path.Combine(windows, "System32", "drivers", "etc", "hosts");
        items.Add(new ExtraItem(ExtraKind.Hosts, hosts, File.Exists(hosts), HostsDetail(hosts)));

        var git = Path.Combine(profile, ".gitconfig");
        items.Add(new ExtraItem(ExtraKind.GitConfig, git, File.Exists(git), ""));

        var psProfiles = new[] { Path.Combine(documents, "PowerShell"), Path.Combine(documents, "WindowsPowerShell") }.Where(Directory.Exists).ToList();
        items.Add(new ExtraItem(ExtraKind.PowerShellProfile, psProfiles.FirstOrDefault(), psProfiles.Count > 0, string.Join("; ", psProfiles.Select(Path.GetFileName))));

        var userPath = Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "";
        items.Add(new ExtraItem(ExtraKind.Path, null, true, $"{userPath.Split(';', StringSplitOptions.RemoveEmptyEntries).Length} user entries"));

        var userVariables = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.User).Count;
        items.Add(new ExtraItem(ExtraKind.Environment, null, true, $"{userVariables} user variables"));

        items.Add(new ExtraItem(ExtraKind.Wifi, null, OperatingSystem.IsWindows(), ""));

        return items;
    }

    private static int CountFiles(string folder)
    {
        try
        {
            return Directory.Exists(folder) ? Directory.GetFiles(folder).Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    /// <summary>The default hosts file is all comments; one with lines in it is one somebody edited.</summary>
    private static string HostsDetail(string path)
    {
        try
        {
            if (!File.Exists(path))
                return "";
            var lines = File.ReadAllLines(path).Count(l => l.Trim() is { Length: > 0 } t && !t.StartsWith('#'));
            return lines == 0 ? "default" : $"{lines} entries";
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>Carries the chosen extras into root\extras and says how each went.</summary>
    public static async Task<IReadOnlyList<ManifestExtra>> CarryAsync(string root, IReadOnlyList<ExtraItem> chosen, CancellationToken token)
    {
        var folder = Path.Combine(root, Folder);
        Directory.CreateDirectory(folder);
        var results = new List<ManifestExtra>();

        foreach (var item in chosen)
        {
            token.ThrowIfCancellationRequested();
            var entry = new ManifestExtra { Kind = item.Kind.ToString(), Source = item.Source };
            try
            {
                switch (item.Kind)
                {
                    case ExtraKind.Fonts:
                    case ExtraKind.PowerShellProfile:
                        entry.Stored = Path.Combine(Folder, item.Kind == ExtraKind.Fonts ? "fonts" : "powershell");
                        if (item.Kind == ExtraKind.PowerShellProfile)
                        {
                            var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
                            var outcomes = new List<CopyOutcome>();
                            foreach (var name in new[] { "PowerShell", "WindowsPowerShell" })
                            {
                                var source = Path.Combine(documents, name);
                                if (Directory.Exists(source))
                                    outcomes.Add(Carry.CopyTree(source, Path.Combine(root, entry.Stored, name), null, token));
                            }
                            entry.Ok = outcomes.All(o => o.Failed.Count == 0);
                            entry.Failure = string.Join("; ", outcomes.SelectMany(o => o.Failed));
                        }
                        else
                        {
                            var outcome = Carry.CopyTree(item.Source!, Path.Combine(root, entry.Stored), null, token);
                            entry.Ok = outcome.Failed.Count == 0;
                            entry.Failure = outcome.Failed.Count == 0 ? null : string.Join("; ", outcome.Failed);
                        }
                        break;

                    case ExtraKind.Hosts:
                        entry.Stored = Path.Combine(Folder, "hosts");
                        File.Copy(item.Source!, Path.Combine(root, entry.Stored), overwrite: true);
                        entry.Ok = true;
                        break;

                    case ExtraKind.GitConfig:
                        entry.Stored = Path.Combine(Folder, ".gitconfig");
                        File.Copy(item.Source!, Path.Combine(root, entry.Stored), overwrite: true);
                        entry.Ok = true;
                        break;

                    case ExtraKind.Path:
                        entry.Stored = Path.Combine(Folder, "path.txt");
                        await File.WriteAllTextAsync(Path.Combine(root, entry.Stored), PathText(), token);
                        entry.Ok = true;
                        break;

                    case ExtraKind.Environment:
                        entry.Stored = Path.Combine(Folder, "environment.txt");
                        await File.WriteAllTextAsync(Path.Combine(root, entry.Stored), EnvironmentText(), token);
                        entry.Ok = true;
                        break;

                    case ExtraKind.Wifi:
                        entry.Stored = Path.Combine(Folder, "wifi");
                        var wifi = Path.Combine(root, entry.Stored);
                        Directory.CreateDirectory(wifi);
                        var result = await Run.CaptureAsync("netsh", ["wlan", "export", "profile", "key=clear", $"folder={wifi}"], token);
                        var exported = Directory.GetFiles(wifi, "*.xml").Length;
                        entry.Ok = result.Started && exported > 0;
                        entry.Note = $"{exported} profiles";
                        if (!entry.Ok)
                            entry.Failure = result.Started ? string.Join(" ", result.Output).Trim() : result.FailureReason;
                        break;
                }
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                entry.Ok = false;
                entry.Failure = ex.Message;
            }
            results.Add(entry);
        }
        return results;
    }

    private static string PathText()
    {
        var lines = new List<string> { "# PATH, user then machine, one entry per line. Not restored automatically: read it, and add back what still exists.", "", "[user]" };
        lines.AddRange((Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        lines.Add("");
        lines.Add("[machine]");
        lines.AddRange((Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine) ?? "").Split(';', StringSplitOptions.RemoveEmptyEntries));
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    private static string EnvironmentText()
    {
        var lines = new List<string> { "# User environment variables, PATH aside. Not restored automatically.", "" };
        var variables = Environment.GetEnvironmentVariables(EnvironmentVariableTarget.User);
        foreach (var key in variables.Keys.Cast<string>().OrderBy(k => k, StringComparer.OrdinalIgnoreCase))
        {
            if (key.Equals("PATH", StringComparison.OrdinalIgnoreCase))
                continue;
            lines.Add($"{key}={variables[key]}");
        }
        return string.Join(Environment.NewLine, lines) + Environment.NewLine;
    }

    /// <summary>The way back for the Wi-Fi profiles: one netsh call per file, and what each said.</summary>
    public static async Task<IReadOnlyList<(string Name, bool Ok, string Said)>> AddWifiProfilesAsync(string folder, CancellationToken token)
    {
        var results = new List<(string, bool, string)>();
        if (!Directory.Exists(folder))
            return results;
        foreach (var file in Directory.GetFiles(folder, "*.xml").OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            token.ThrowIfCancellationRequested();
            var result = await Run.CaptureAsync("netsh", ["wlan", "add", "profile", $"filename={file}", "user=all"], token);
            var said = result.Started ? string.Join(" ", result.Output).Trim() : result.FailureReason ?? "";
            var name = Path.GetFileNameWithoutExtension(file);
            if (name.StartsWith("Wi-Fi-", StringComparison.OrdinalIgnoreCase) || name.StartsWith("WLAN-", StringComparison.OrdinalIgnoreCase))
                name = name[(name.IndexOf('-') + 1)..];
            results.Add((name, result.Succeeded, said));
        }
        return results;
    }
}
