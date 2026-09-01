using Mews.Disk;

namespace Mews.Plugins.Molt.Services;

/// <summary>
/// One thing that can be shed, with what it is and what losing it actually costs. The cost line
/// is not decoration: "safe to delete" is a claim, and a tool making that claim about someone
/// else's disk owes them the reasoning.
/// </summary>
public sealed class Sheddable
{
    public required string Id { get; init; }

    public required string Name { get; init; }

    /// <summary>Where it lives, shown so the claim can be checked rather than trusted.</summary>
    public required string Where { get; init; }

    /// <summary>What it is, in one line.</summary>
    public required string What { get; init; }

    /// <summary>What it costs to lose it. Never "nothing", because that is never quite true.</summary>
    public required string Cost { get; init; }

    /// <summary>The actual files and folders that would go.</summary>
    public List<string> Paths { get; } = [];

    public long Size { get; set; }

    public bool Exists => Paths.Count > 0;
}

public sealed record MoltOptions
{
    /// <summary>Temp entries younger than this are left alone: something may still be using them.</summary>
    public int TempOlderThanDays { get; init; } = 7;

    /// <summary>Where to look for bin and obj folders. Nothing is searched until this is set.</summary>
    public string? BuildRoot { get; init; }

    /// <summary>
    /// Where the caches live. Overridable so the catalogue is a function of what it is given
    /// rather than of whatever happens to be on this machine, which makes it both testable and
    /// quick to test: measuring a real NuGet package folder takes long enough to notice.
    /// </summary>
    public string? LocalAppData { get; init; }

    public string? UserProfile { get; init; }

    public string? TempFolder { get; init; }
}

/// <summary>
/// Works out what can be shed and how much it is worth. Nothing here removes anything: it
/// produces a list of candidates with the reasoning attached, and removing them is a separate,
/// deliberate step.
/// </summary>
public static class MoltCatalog
{
    /// <summary>
    /// Folders never worth walking into when hunting for build output. Unity's Library and Temp
    /// are on here for a reason worth knowing: a single Unity project can hold tens of thousands
    /// of folders under them, and walking those was the difference between this taking a second
    /// and taking minutes. They are not ignored, they are collected whole further down.
    /// </summary>
    private static readonly string[] SkipWhileHunting =
    [
        ".git", "node_modules", ".vs", ".idea", "packages",
        "Library", "Temp", "Logs", ".venv", "venv", "__pycache__", ".gradle", "target",
    ];

    /// <summary>How deep to hunt. Build output lives near a project root, never twenty down.</summary>
    private const int MaxDepth = 8;

    public static IReadOnlyList<Sheddable> Build(
        MoltOptions options, IProgress<string>? progress, CancellationToken token)
    {
        var local = options.LocalAppData
                    ?? Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var profile = options.UserProfile
                      ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        var all = new List<Sheddable>
        {
            Temp(options, token),
            Simple("nuget-http", "NuGet download cache", Path.Combine(local, "NuGet", "v3-cache"),
                "Packages NuGet kept a copy of after downloading them.",
                "Nothing, beyond a slower first restore while they are fetched again."),
            Simple("nuget-packages", "NuGet global packages", Path.Combine(profile, ".nuget", "packages"),
                "Every package version any project on this machine has ever restored.",
                "Every project has to download its packages again on the next restore. Safe, but not quick, and useless without an internet connection."),
            Simple("npm", "npm cache", Path.Combine(local, "npm-cache"),
                "What npm keeps so it does not fetch the same tarball twice.",
                "Nothing, beyond a slower first install."),
            Simple("pip", "pip cache", Path.Combine(local, "pip", "Cache"),
                "Wheels pip kept after building or downloading them.",
                "Nothing, beyond a slower first install."),
            Simple("dumps", "Crash dumps", Path.Combine(local, "CrashDumps"),
                "Memory dumps written when something crashed.",
                "Nothing, unless you are in the middle of investigating a crash."),
        };

        all.AddRange(Hunt(options, progress, token));
        return all.Where(s => s.Exists).ToList();
    }

    /// <summary>
    /// Temp, but only the parts old enough to be certainly abandoned. Anything touched recently
    /// may belong to something still running, and taking it is how a tool like this earns a
    /// reputation for breaking things.
    /// </summary>
    private static Sheddable Temp(MoltOptions options, CancellationToken token)
    {
        var entry = new Sheddable
        {
            Id = "temp",
            Name = $"Windows temp, older than {options.TempOlderThanDays} days",
            Where = TempOf(options),
            What = "Scratch files programs wrote and did not clean up.",
            Cost = "Nothing. Anything touched in the last few days is left alone in case it is still in use.",
        };

        var cutoff = DateTime.Now.AddDays(-options.TempOlderThanDays);

        try
        {
            var root = new DirectoryInfo(TempOf(options));
            if (!root.Exists)
                return entry;

            foreach (var file in root.EnumerateFiles())
            {
                token.ThrowIfCancellationRequested();
                if (file.LastWriteTime >= cutoff)
                    continue;

                entry.Paths.Add(file.FullName);
                try
                {
                    entry.Size += file.Length;
                }
                catch (Exception)
                {
                }
            }

            foreach (var folder in root.EnumerateDirectories())
            {
                token.ThrowIfCancellationRequested();
                if (folder.LastWriteTime >= cutoff || !WalkRules.ShouldDescend(folder, skipSystemFolders: false))
                    continue;

                entry.Paths.Add(folder.FullName);
                entry.Size += FolderSize.Of(folder);
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
        }

        return entry;
    }

    /// <summary>A cache that is simply one folder: take everything inside, keep the folder.</summary>
    private static Sheddable Simple(string id, string name, string path, string what, string cost)
    {
        var entry = new Sheddable { Id = id, Name = name, Where = path, What = what, Cost = cost };

        try
        {
            var root = new DirectoryInfo(path);
            if (!root.Exists)
                return entry;

            // The contents, not the folder itself. Tools tend to assume their cache folder
            // exists and are happier finding it empty than missing.
            foreach (var child in root.EnumerateFileSystemInfos())
            {
                entry.Paths.Add(child.FullName);
                entry.Size += child is DirectoryInfo directory
                    ? FolderSize.Of(directory)
                    : SafeLength(child);
            }
        }
        catch (Exception)
        {
        }

        return entry;
    }

    /// <summary>
    /// One walk of the projects folder, producing both the compiler output and the Unity caches.
    /// A found folder is taken whole and never descended into: the entire thing goes, so counting
    /// what is inside it twice would only make the hunt slower.
    /// </summary>
    private static List<Sheddable> Hunt(MoltOptions options, IProgress<string>? progress, CancellationToken token)
    {
        var build = new Sheddable
        {
            Id = "build",
            Name = "bin and obj folders",
            Where = options.BuildRoot ?? "no folder chosen yet",
            What = "Compiler output under every project in the folder you pick.",
            Cost = "The next build of each project is a full one rather than an incremental one.",
        };

        var unity = new Sheddable
        {
            Id = "unity",
            Name = "Unity Library and Temp folders",
            Where = options.BuildRoot ?? "no folder chosen yet",
            What = "What Unity imports every asset into so it does not have to do it again.",
            Cost = "Unity reimports the whole project the next time it opens it, which on a large project is a long coffee.",
        };

        if (string.IsNullOrWhiteSpace(options.BuildRoot) || !Directory.Exists(options.BuildRoot))
            return [build, unity];

        var stack = new Stack<(DirectoryInfo Folder, int Depth)>();
        stack.Push((new DirectoryInfo(options.BuildRoot), 0));
        var seen = 0;

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (current, depth) = stack.Pop();

            DirectoryInfo[] children;
            try
            {
                children = current.GetDirectories();
            }
            catch (Exception)
            {
                continue;
            }

            if (++seen % 200 == 0)
                progress?.Report($"{seen} folders searched");

            // Assets and ProjectSettings together is how a Unity project announces itself.
            var isUnityProject =
                children.Any(c => c.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase)) &&
                children.Any(c => c.Name.Equals("ProjectSettings", StringComparison.OrdinalIgnoreCase));

            foreach (var child in children)
            {
                if (!WalkRules.ShouldDescend(child, skipSystemFolders: false))
                    continue;

                if (child.Name is "bin" or "obj")
                {
                    build.Paths.Add(child.FullName);
                    build.Size += FolderSize.Of(child);
                    continue;
                }

                if (isUnityProject && child.Name is "Library" or "Temp")
                {
                    unity.Paths.Add(child.FullName);
                    unity.Size += FolderSize.Of(child);
                    continue;
                }

                if (SkipWhileHunting.Contains(child.Name, StringComparer.OrdinalIgnoreCase))
                    continue;

                if (depth < MaxDepth)
                    stack.Push((child, depth + 1));
            }
        }

        return [build, unity];
    }

    private static string TempOf(MoltOptions options) => options.TempFolder ?? Path.GetTempPath();

    private static long SafeLength(FileSystemInfo info)
    {
        try
        {
            return info is FileInfo file ? file.Length : 0;
        }
        catch (Exception)
        {
            return 0;
        }
    }
}
