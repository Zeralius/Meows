using Meows.Plugins.Abstractions;

namespace Meows.Disk;

/// <summary>
/// What an archive is, the way <see cref="FolderInspector"/> says what a folder is: what it holds,
/// and whether the folder beside it already holds the same thing. The twin case is the one worth
/// having, because an archive sitting next to its own extracted contents is room paid for twice
/// and nothing else on the drive says so.
/// </summary>
public static class ArchiveInspector
{
    private static string Say(string key) => MeowsText.Current[key];

    private static string Say(string key, params object?[] values) => MeowsText.Current.Format(key, values);

    /// <summary>The archive side. Called for a file that <see cref="Archives.IsArchive"/> says yes to.</summary>
    public static FolderIdentity Of(string path, CancellationToken token = default)
    {
        var evidence = new List<string>();
        var summary = Archives.Peek(path);
        var sibling = Archives.SiblingFolderOf(path);

        if (summary is null)
        {
            if (sibling is not null)
                evidence.Add(Say("disk.archive.sibling.unchecked", Path.GetFileName(sibling)));

            return new FolderIdentity(
                Say("disk.archive.headline.opaque"),
                FolderVerdict.Archive,
                Say("disk.archive.advice.opaque"),
                evidence,
                false);
        }

        evidence.Add(Contents(summary));
        evidence.AddRange(Wound(path, summary));

        if (sibling is not null)
        {
            var twin = Archives.Compare(path, sibling, token);
            if (twin.IsTwin)
                return ForArchive(twin, evidence);

            evidence.Add(NotTwin(twin));
        }

        return new FolderIdentity(
            Say("disk.archive.headline", summary.EntryCount),
            FolderVerdict.Archive,
            Say("disk.archive.advice"),
            evidence,
            false);
    }

    private static FolderIdentity ForArchive(TwinReport twin, List<string> evidence)
    {
        evidence.Add(Cost(twin));

        return new FolderIdentity(
            Say("disk.twin.archive.headline", Path.GetFileName(twin.FolderPath)),
            FolderVerdict.Twin,
            Advice(twin),
            evidence,
            false) { Twin = twin };
    }

    /// <summary>The folder side: this folder is what the archive beside it unpacks to.</summary>
    public static FolderIdentity ForFolder(TwinReport twin, bool inUse) =>
        new(
            Say("disk.twin.folder.headline", Path.GetFileName(twin.ArchivePath)),
            FolderVerdict.Twin,
            Advice(twin),
            [Cost(twin)],
            inUse) { Twin = twin };

    /// <summary>
    /// One line for a same-named pair that turned out not to be the same. Worth saying, because
    /// the name alone would have had anyone believe it was.
    /// </summary>
    public static string NotTwin(TwinReport twin) => twin.Verdict == TwinVerdict.Unreadable
        ? Say("disk.archive.sibling.unchecked", Path.GetFileName(twin.FolderPath))
        : Say("disk.archive.sibling.differs", Path.GetFileName(twin.FolderPath), twin.Missing, twin.Different);

    private static string Advice(TwinReport twin) => twin.Extra > 0
        ? Say("disk.twin.advice.extras", twin.Matched, twin.Extra)
        : Say("disk.twin.advice", twin.Matched);

    private static string Cost(TwinReport twin) =>
        Say("disk.twin.cost", FolderSize.Humanise(twin.ArchiveSize), FolderSize.Humanise(twin.MatchedBytes));

    /// <summary>
    /// Twine's two questions, answered from the same table of contents: is there an archive in
    /// this archive, and is there only one file. Said, never acted on. A single page in a .cbz is
    /// often a comic of one page on purpose, so that one is said more softly.
    /// </summary>
    public static IEnumerable<string> Wound(string path, ArchiveSummary summary)
    {
        if (summary.OnlyEntry is { } only)
        {
            var name = Path.GetFileName(only.Replace('/', Path.DirectorySeparatorChar));
            if (Archives.IsArchive(only))
                yield return Say("disk.twine.only.archive", name, FolderSize.Humanise(summary.UnpackedSize));
            else if (IsComic(path))
                yield return Say("disk.twine.only.page", name);
            else
                yield return Say("disk.twine.only", name);
            yield break;
        }

        switch (summary.Nested.Count)
        {
            case 0:
                break;
            case 1:
                yield return Say("disk.twine.nested.one", Path.GetFileName(summary.Nested[0].Name.Replace('/', Path.DirectorySeparatorChar)), FolderSize.Humanise(summary.NestedBytes));
                break;
            default:
                yield return Say("disk.twine.nested", summary.Nested.Count, FolderSize.Humanise(summary.NestedBytes));
                break;
        }
    }

    private static bool IsComic(string path) =>
        Path.GetExtension(path) is { } ext && (ext.Equals(".cbz", StringComparison.OrdinalIgnoreCase) || ext.Equals(".cbr", StringComparison.OrdinalIgnoreCase));

    private static string Contents(ArchiveSummary summary) => summary.TopKind is "" or "(none)"
        ? Say("disk.archive.contents.noextension", summary.EntryCount, FolderSize.Humanise(summary.UnpackedSize))
        : Say("disk.archive.contents", summary.EntryCount, FolderSize.Humanise(summary.UnpackedSize), summary.TopShare, summary.TopKind);
}
