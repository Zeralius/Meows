using Meows.Disk;

namespace Meows.Plugins.Mouser.Services;

/// <summary>The kinds of dead weight Mouser looks for.</summary>
public enum DeadKind
{
    EmptyFolder,
    EmptyFile,
    BrokenShortcut,
    Leftover,
}

/// <summary>
/// One thing worth removing. Detail is a key from the strings catalogue rather than a sentence,
/// with DetailValues holding anything that goes into its placeholders: the scan runs off the UI
/// thread and has no business knowing what language the window is in.
/// </summary>
public sealed record Finding(string Path, string Name, DeadKind Kind, string Detail, long Size)
{
    public object?[] DetailValues { get; init; } = [];
}

public sealed record MouserOptions
{
    public bool SkipSystemFolders { get; init; } = true;

    /// <summary>Files Windows and macOS leave behind and nothing needs.</summary>
    public static readonly string[] LeftoverNames = ["Thumbs.db", "ehthumbs.db", ".DS_Store"];

    /// <summary>
    /// Files that are meant to be empty, where size tells you nothing about whether they are
    /// wanted. An empty __init__.py is what makes a Python package a package, and .gitkeep only
    /// exists so git keeps the folder around it.
    /// </summary>
    public static readonly string[] MeantToBeEmptyNames =
    [
        "__init__.py", "__init__.pyi", "py.typed", ".gitkeep", ".keep", ".placeholder",
        ".nojekyll", ".metadata_never_index", ".localized", ".empty",
    ];

    /// <summary>
    /// Extensions used as markers, where the file existing is the whole point. Unity writes
    /// thousands of these into a project and they are all doing their job.
    /// </summary>
    public static readonly string[] MeantToBeEmptyExtensions =
    [
        ".mvfrm", ".modulecompilationtrigger", ".lock", ".stamp",
    ];

    /// <summary>Whether a zero byte file is empty on purpose.</summary>
    public static bool IsMeantToBeEmpty(string name)
    {
        if (MeantToBeEmptyNames.Contains(name, StringComparer.OrdinalIgnoreCase))
            return true;

        var extension = Path.GetExtension(name).ToLowerInvariant();

        // No extension and no contents: names like REQUESTED, WEBGL_SUPPORTED or CodeSignature
        // are programs leaving themselves a marker. Skipping them costs nothing, since a zero
        // byte file frees no space, and deleting one could break whatever wrote it.
        return extension.Length == 0 || MeantToBeEmptyExtensions.Contains(extension);
    }
}

public sealed record MouserProgress(int FoldersSeen, int Found, string Current);

/// <summary>
/// What a sweep found, and whether it finished. Stopping early is a normal outcome rather than
/// a failure, so the results are still worth showing as long as the UI says they are partial.
/// </summary>
public sealed record ScanResult(IReadOnlyList<Finding> Findings, bool WasStopped, int FoldersSeen);

/// <summary>
/// Finds files and folders that serve no purpose. Chonk finds what is big and Purrge finds what
/// is duplicated; none of this is either, which is why nothing else catches it and why a drive
/// accumulates thousands.
///
/// Every finding carries the reason it was picked, and anything we cannot justify is not
/// reported at all. For a tool that deletes, a false positive is much worse than a miss.
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

        // Discovery order, so parents come before children and the emptiness roll up is just a
        // backwards pass over this list.
        var folders = new List<DirectoryInfo>();
        var hasContent = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        var parents = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);

        var stack = new Stack<DirectoryInfo>();
        var start = new DirectoryInfo(root);
        stack.Push(start);
        parents[start.FullName] = null;

        var seen = 0;

        // Where the walk stopped, if it did. That folder is half read and everything still
        // queued is unread, so none of them can be judged.
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
                // Unreadable, so say nothing rather than guess it is empty.
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

            // Anything we did not step into leaves the contents partly unknown, so treat the
            // folder as occupied rather than calling it empty without evidence.
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

        // Anything still queued was never looked at, and that propagates upwards: a folder is
        // not empty if any part of it went unread. Marking the ancestors is what stops a
        // cancelled sweep offering to delete a folder that is actually full.
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

        // Backwards, so a folder containing only empty folders counts as empty too.
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
            // Never offer the folder that was passed in. Nobody asked to delete that.
            if (!Offerable(folder.FullName))
                continue;

            // Only the outermost of a run of nested empty folders, since deleting it takes the
            // rest. Listing them all is noise and every delete after the first would target
            // something already gone. The parent has to be offerable itself, or the children of
            // an empty root would be skipped as well.
            if (parents.TryGetValue(folder.FullName, out var above) && above is not null && Offerable(above))
                continue;

            findings.Add(new Finding(folder.FullName, folder.Name, DeadKind.EmptyFolder,
                "mouser.detail.emptyfolder", 0));
        }

        progress?.Report(new MouserProgress(seen, findings.Count, root));
        return new ScanResult(findings, stopped, seen);
    }

    /// <summary>
    /// How many subfolders exist, against how many the walk would enter. The difference is what
    /// was skipped, and a folder containing only skipped things is not empty.
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

    /// <summary>What is wrong with this file, if anything. Null means nothing is.</summary>
    private static Finding? Inspect(FileInfo file)
    {
        if (MouserOptions.LeftoverNames.Contains(file.Name, StringComparer.OrdinalIgnoreCase))
        {
            return new Finding(file.FullName, file.Name, DeadKind.Leftover,
                "mouser.detail.leftover", SafeLength(file));
        }

        if (file.Extension.Equals(".lnk", StringComparison.OrdinalIgnoreCase))
        {
            var target = ShellLink.TargetOf(file.FullName);

            // Leave a shortcut alone if we cannot read its target. Not knowing where it points
            // is not the same as knowing it points nowhere.
            if (target is null || File.Exists(target) || Directory.Exists(target))
                return null;

            return new Finding(file.FullName, file.Name, DeadKind.BrokenShortcut,
                "mouser.detail.brokenshortcut", SafeLength(file)) { DetailValues = [target] };
        }

        if (SafeLength(file) == 0 && !MouserOptions.IsMeantToBeEmpty(file.Name))
        {
            return new Finding(file.FullName, file.Name, DeadKind.EmptyFile,
                "mouser.detail.emptyfile", 0);
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

    /// <summary>The key for a filter heading, looked up wherever it is actually drawn.</summary>
    public static string Describe(DeadKind kind) => kind switch
    {
        DeadKind.EmptyFolder => "mouser.kind.emptyfolder",
        DeadKind.EmptyFile => "mouser.kind.emptyfile",
        DeadKind.BrokenShortcut => "mouser.kind.brokenshortcut",
        _ => "mouser.kind.leftover",
    };
}
