using Mews.Disk;

namespace Mews.Plugins.Mouser.Services;

/// <summary>The kinds of dead weight worth finding.</summary>
public enum DeadKind
{
    EmptyFolder,
    EmptyFile,
    BrokenShortcut,
    Leftover,
}

public sealed record Finding(string Path, string Name, DeadKind Kind, string Detail, long Size);

public sealed record MouserOptions
{
    public bool SkipSystemFolders { get; init; } = true;

    /// <summary>Files Windows and macOS leave behind that nothing wants.</summary>
    public static readonly string[] LeftoverNames = ["Thumbs.db", "ehthumbs.db", ".DS_Store"];

    /// <summary>
    /// Files whose whole job is to be empty. Being zero bytes is what they are for, so size says
    /// nothing at all about whether they are wanted. An empty __init__.py is what makes a Python
    /// package a package, and a .gitkeep exists only so git will carry the folder around it.
    /// </summary>
    public static readonly string[] MeantToBeEmptyNames =
    [
        "__init__.py", "__init__.pyi", "py.typed", ".gitkeep", ".keep", ".placeholder",
        ".nojekyll", ".metadata_never_index", ".localized", ".empty",
    ];

    /// <summary>
    /// Extensions used purely as markers, where the file existing is the entire message. Unity
    /// writes thousands of these into a project and every one of them is doing its job.
    /// </summary>
    public static readonly string[] MeantToBeEmptyExtensions =
    [
        ".mvfrm", ".modulecompilationtrigger", ".lock", ".stamp",
    ];

    /// <summary>Whether a zero byte file is that way on purpose.</summary>
    public static bool IsMeantToBeEmpty(string name)
    {
        if (MeantToBeEmptyNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return true;

        var extension = Path.GetExtension(name).ToLowerInvariant();

        // No extension at all and no contents either, so there is nothing here that was ever
        // opened by anything: names like REQUESTED, WEBGL_SUPPORTED or CodeSignature are programs
        // leaving themselves a note. Passing over them costs nothing, because a zero byte file
        // takes up no room worth reclaiming, and offering one risks breaking whatever wrote it.
        return extension.Length == 0 || MeantToBeEmptyExtensions.Contains(extension);
    }
}

public sealed record MouserProgress(int FoldersSeen, int Found, string Current);

/// <summary>
/// What a sweep turned up, and whether it got to the end. Stopping early is an ordinary outcome
/// rather than a failure: what was found up to that point is still worth showing, so long as it
/// is clear that the list is only as far as it got.
/// </summary>
public sealed record ScanResult(IReadOnlyList<Finding> Findings, bool WasStopped, int FoldersSeen);

/// <summary>
/// Finds what is simply pointless. Chonk answers what is big and Purrge answers what is
/// duplicated; none of what turns up here is either, which is exactly why nothing else finds it
/// and why a drive quietly ends up with thousands of them.
///
/// Everything reported has to be defensible on its own, so each finding carries the reason it
/// was picked. Where that reason cannot be established, nothing is reported: a tool that removes
/// dead things has to be far more afraid of a false positive than of missing one.
/// </summary>
public static class MouserScan
{
    private const int ReportEvery = 150;

    public static ScanResult Run(
        string root,
        MouserOptions options,
        IProgress<MouserProgress>? progress,
        CancellationToken token)
    {
        var findings = new List<Finding>();

        if (!Directory.Exists(root))
            return new ScanResult(findings, false, 0);

        // Discovery order, so a parent is always seen before its children and the emptiness
        // roll up is a walk backwards through the same list.
        var folders = new List<DirectoryInfo>();
        var hasContent = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var parents = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var stack = new Stack<DirectoryInfo>();
        var start = new DirectoryInfo(root);
        stack.Push(start);
        parents[start.FullName] = null;

        var seen = 0;

        // Where the walk gave up, if it did. Stopping leaves this folder half read and everything
        // still queued untouched, and none of those can be judged.
        DirectoryInfo? gaveUpAt = null;

        while (stack.Count > 0)
        {
            if (token.IsCancellationRequested)
                break;

            var current = stack.Pop();
            folders.Add(current);
            hasContent.TryAdd(current.FullName, false);
            seen++;

            if (!FolderWalk.CanRead(current))
            {
                // Unreadable. Say nothing about it rather than guess it is empty.
                hasContent[current.FullName] = true;
                continue;
            }

            var files = FolderWalk.Files(current);

            foreach (var file in files)
            {
                if (token.IsCancellationRequested)
                {
                    gaveUpAt = current;
                    break;
                }

                hasContent[current.FullName] = true;

                var finding = Inspect(file);
                if (finding is not null)
                    findings.Add(finding);
            }

            if (gaveUpAt is not null)
                break;

            var children = FolderWalk.Into(current, options.SkipSystemFolders);

            // Anything the walk will not step into leaves this folder's contents partly unknown,
            // so it counts as occupied rather than being called empty on no evidence.
            if (children.Count != CountOfChildren(current))
                hasContent[current.FullName] = true;

            foreach (var child in children)
            {
                parents[child.FullName] = current.FullName;
                stack.Push(child);
            }

            if (progress is not null && seen % ReportEvery == 0)
                progress.Report(new MouserProgress(seen, findings.Count, current.FullName));
        }

        // Anything still queued was never looked at, so nothing is known about what is in it, and
        // that ignorance travels all the way up: a folder cannot be called empty while any part
        // of what is below it went unread. Marking the ancestors is what stops a stopped sweep
        // from offering to delete a folder that is actually full.
        var unread = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void MarkUnread(string path)
        {
            var at = path;
            while (at is not null && unread.Add(at))
            {
                if (!parents.TryGetValue(at, out at))
                    break;
            }
        }

        foreach (var queued in stack)
            MarkUnread(queued.FullName);

        if (gaveUpAt is not null)
            MarkUnread(gaveUpAt.FullName);

        var stopped = unread.Count > 0;

        // Backwards, so a folder holding only empty folders counts as empty too.
        for (var i = folders.Count - 1; i >= 0; i--)
        {
            var folder = folders[i];
            if (!hasContent[folder.FullName])
                continue;

            if (parents.TryGetValue(folder.FullName, out var parent) && parent is not null)
                hasContent[parent] = true;
        }

        bool Offerable(string path) =>
            hasContent.TryGetValue(path, out var occupied) && !occupied &&
            !unread.Contains(path) &&
            !path.Equals(start.FullName, StringComparison.OrdinalIgnoreCase);

        foreach (var folder in folders)
        {
            // The folder handed in is never offered: nobody asked to delete what they pointed at.
            if (!Offerable(folder.FullName))
                continue;

            // Only the topmost one of a run of nested empties, since removing that takes the rest
            // with it. Listing all of them is noise, and every delete after the first would be of
            // something already gone. The parent has to be offerable itself for this to apply:
            // when the root is the empty one, its children are the topmost thing on offer.
            if (parents.TryGetValue(folder.FullName, out var above) && above is not null && Offerable(above))
                continue;

            findings.Add(new Finding(folder.FullName, folder.Name, DeadKind.EmptyFolder,
                "Nothing inside it, at any depth.", 0));
        }

        progress?.Report(new MouserProgress(seen, findings.Count, root));
        return new ScanResult(findings, stopped, seen);
    }

    /// <summary>
    /// How many subfolders are actually there, against how many the walk agreed to enter. The
    /// difference is what was skipped, and a folder holding only skipped things is not empty.
    /// </summary>
    private static int CountOfChildren(DirectoryInfo directory)
    {
        try
        {
            return directory.GetDirectories().Length;
        }
        catch (Exception)
        {
            return int.MaxValue;
        }
    }

    /// <summary>What is wrong with this file, if anything.</summary>
    private static Finding? Inspect(FileInfo file)
    {
        if (MouserOptions.LeftoverNames.Contains(file.Name, StringComparer.OrdinalIgnoreCase))
        {
            return new Finding(file.FullName, file.Name, DeadKind.Leftover,
                "Left behind by a file browser. Rebuilt whenever it is wanted again.", SafeLength(file));
        }

        if (file.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var target = ShellLink.TargetOf(file.FullName);

            // A shortcut whose target cannot be read is left alone. Not knowing is not the same
            // as knowing it is dead.
            if (target is null || File.Exists(target) || Directory.Exists(target))
                return null;

            return new Finding(file.FullName, file.Name, DeadKind.BrokenShortcut,
                $"Points at {target}, which is not there.", SafeLength(file));
        }

        if (SafeLength(file) == 0 && !MouserOptions.IsMeantToBeEmpty(file.Name))
        {
            return new Finding(file.FullName, file.Name, DeadKind.EmptyFile,
                "No contents at all.", 0);
        }

        return null;
    }

    private static long SafeLength(FileInfo file)
    {
        try
        {
            return file.Length;
        }
        catch (Exception)
        {
            return 0;
        }
    }

    public static string Describe(DeadKind kind) => kind switch
    {
        DeadKind.EmptyFolder => "Empty folders",
        DeadKind.EmptyFile => "Empty files",
        DeadKind.BrokenShortcut => "Broken shortcuts",
        _ => "Leftovers",
    };
}
