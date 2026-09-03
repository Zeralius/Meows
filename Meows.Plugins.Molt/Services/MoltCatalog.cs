using Meows.Disk;

namespace Meows.Plugins.Molt.Services;

/// <summary>
/// One thing that can be shed, with what it is and what losing it costs. The cost line matters:
/// "safe to delete" is a claim, and we should show the reasoning behind it.
/// </summary>
public sealed class Sheddable
{
    public required string Id { get; init; }

    /// <summary>
    /// A key from the strings catalogue rather than a sentence. The rows are built here, off the
    /// UI thread and with no idea what language the window is in, so the view model looks them up
    /// when it shows them. The same goes for What, Cost and Where.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>What goes into the placeholders in Name, if it has any.</summary>
    public object?[] NameValues { get; init; } = [];

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
    /// Where the caches live. Overridable so the catalogue depends on what it is given rather
    /// than on this machine, which makes it testable and fast: measuring a real NuGet package
    /// folder takes long enough to be annoying in a test.
    /// </summary>
    public string? LocalAppData { get; init; }

    public string? UserProfile { get; init; }

    public string? TempFolder { get; init; }
}

/// <summary>
/// Works out what can be shed and how much it would free. Nothing here deletes: it returns
/// candidates with the reasoning attached, and removing them is a separate step.
/// </summary>
public static class MoltCatalog
{
    /// <summary>
    /// Folders not worth walking into when hunting for build output. Unity's Library and Temp
    /// are here because one Unity project can hold tens of thousands of folders under them, which
    /// was the difference between this taking a second and taking minutes. They are not ignored,
    /// just collected whole further down.
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
            Simple("nuget-http", "molt.nuget-http.name", Path.Combine(local, "NuGet", "v3-cache"),
                "molt.nuget-http.what", "molt.nuget-http.cost"),
            Simple("nuget-packages", "molt.nuget-packages.name", Path.Combine(profile, ".nuget", "packages"),
                "molt.nuget-packages.what", "molt.nuget-packages.cost"),
            Simple("npm", "molt.npm.name", Path.Combine(local, "npm-cache"),
                "molt.npm.what", "molt.npm.cost"),
            Simple("pip", "molt.pip.name", Path.Combine(local, "pip", "Cache"),
                "molt.pip.what", "molt.pip.cost"),
            Simple("dumps", "molt.dumps.name", Path.Combine(local, "CrashDumps"),
                "molt.dumps.what", "molt.dumps.cost"),
        };

        all.AddRange(Hunt(options, progress, token));
        return all.Where(s => s.Exists).ToList();
    }

    /// <summary>
    /// Temp, but only the parts old enough to be safely abandoned. Anything touched recently
    /// may belong to a running program, and deleting that is how this kind of tool breaks
    /// things.
    /// </summary>
    private static Sheddable Temp(MoltOptions options, CancellationToken token)
    {
        var entry = new Sheddable
        {
            Id = "temp",
            Name = "molt.temp.name",
            NameValues = [options.TempOlderThanDays],
            Where = TempOf(options),
            What = "molt.temp.what",
            Cost = "molt.temp.cost",
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

            foreach (var folder in FolderWalk.Into(root, skipSystemFolders: false))
            {
                token.ThrowIfCancellationRequested();
                if (folder.LastWriteTime >= cutoff)
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
    /// One walk of the projects folder, collecting both compiler output and Unity caches. A
    /// match is taken whole and never descended into, since the whole thing goes anyway and
    /// walking inside it would only slow the hunt down.
    /// </summary>
    private static List<Sheddable> Hunt(MoltOptions options, IProgress<string>? progress, CancellationToken token)
    {
        var build = new Sheddable
        {
            Id = "build",
            Name = "molt.build.name",
            Where = options.BuildRoot ?? "molt.nofolder",
            What = "molt.build.what",
            Cost = "molt.build.cost",
        };

        var unity = new Sheddable
        {
            Id = "unity",
            Name = "molt.unity.name",
            Where = options.BuildRoot ?? "molt.nofolder",
            What = "molt.unity.what",
            Cost = "molt.unity.cost",
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

            // Every child, for working out what kind of project this is, and separately the ones
            // actually worth stepping into.
            DirectoryInfo[] children;
            try
            {
                children = current.GetDirectories();
            }
            catch (Exception)
            {
                continue;
            }

            var walkable = FolderWalk.Into(current, skipSystemFolders: false);

            if (++seen % 200 == 0)
                progress?.Report($"{seen} folders searched");

            // Assets and ProjectSettings together is how a Unity project announces itself.
            var isUnityProject =
                children.Any(c => c.Name.Equals("Assets", StringComparison.OrdinalIgnoreCase)) &&
                children.Any(c => c.Name.Equals("ProjectSettings", StringComparison.OrdinalIgnoreCase));

            foreach (var child in walkable)
            {
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
