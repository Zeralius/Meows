namespace Meows.Plugins.Abstractions;

/// <summary>Where a copy to the server has got to, for the Tasks panel.</summary>
/// <param name="Done">Files finished, including the one just finished.</param>
/// <param name="Total">Files in the copy.</param>
/// <param name="File">The one being sent, relative to the folder being copied.</param>
public sealed record ReachProgress(int Done, int Total, string File);

/// <summary>How a copy to the server went.</summary>
/// <param name="Ok">Every file is there at the size it has here.</param>
/// <param name="Files">How many were sent.</param>
/// <param name="Bytes">How much, in total.</param>
/// <param name="Where">Where it landed, as a person would recognise it.</param>
/// <param name="Error">Why not, when not: the server could not be reached, a file would not go.</param>
public sealed record ReachResult(bool Ok, int Files, long Bytes, string Where, string? Error = null);

/// <summary>
/// The one machine past this one that Meows knows about: the server the bot and Foundry run
/// on, set once on the Settings tab as a folder (a share, a mapped drive, anything mounted) or
/// as SFTP with a key. A plugin never sees which; it names a place under the server's root and
/// hands over a folder. Since 1.3.0.
/// </summary>
public interface IMeowsReach
{
    /// <summary>Whether a server is set. When false every copy is refused, so keep the button that would ask for one off.</summary>
    bool IsSet { get; }

    /// <summary>The server as a person would recognise it: <c>\\nas\foundry</c>, <c>sftp://me@nas/srv/foundry</c>. Null when not set.</summary>
    string? Where { get; }

    /// <summary>
    /// Copies everything in <paramref name="localFolder"/>, subfolders and all, to
    /// <paramref name="remotePath"/> under the server's root, making the folders it needs. A
    /// file already there under the same name is replaced; anything else there is left alone.
    /// Each file is written under a temporary name and put in place only once whole, and its
    /// size is checked after, so a copy cut off halfway never leaves a half file under the real
    /// name.
    ///
    /// <paramref name="remotePath"/> is relative, with forward slashes: <c>Data/modules/x</c>.
    /// Anything rooted or climbing out with <c>..</c> is refused. Slow, so call it from
    /// background work. Never throws for the server's reasons, which are in the result; throws
    /// only when <paramref name="token"/> is cancelled.
    /// </summary>
    Task<ReachResult> CopyFolder(string localFolder, string remotePath,
        IProgress<ReachProgress>? progress = null, CancellationToken token = default);
}

/// <summary>What a shell built against an older contract answers. There is no server.</summary>
public sealed class NoReach : IMeowsReach
{
    public static NoReach Instance { get; } = new();

    public bool IsSet => false;

    public string? Where => null;

    public Task<ReachResult> CopyFolder(string localFolder, string remotePath,
        IProgress<ReachProgress>? progress = null, CancellationToken token = default) =>
        Task.FromResult(new ReachResult(false, 0, 0, remotePath, "This Meows cannot reach a server. Update Meows."));
}
