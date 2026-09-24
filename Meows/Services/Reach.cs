using System.Text;
using Meows.Plugins.Abstractions;
using Renci.SshNet;

namespace Meows.Services;

/// <summary>How the server is reached. Saved with the preferences; the key and its passphrase are not.</summary>
public sealed class ReachSettings
{
    /// <summary><see cref="ReachKinds.None"/>, <see cref="ReachKinds.Folder"/> or <see cref="ReachKinds.Sftp"/>.</summary>
    public string Kind { get; set; } = ReachKinds.None;

    /// <summary>For a folder: the share, the mapped drive, the mounted path. <c>\\nas\foundry</c>, <c>Z:\</c>.</summary>
    public string? Folder { get; set; }

    public string? Host { get; set; }

    public int Port { get; set; } = 22;

    public string? User { get; set; }

    /// <summary>For SFTP: the folder on the server that plugins' paths are under, <c>/srv/foundry</c>.</summary>
    public string? RemoteRoot { get; set; }

    /// <summary>
    /// The SHA256 fingerprint of the server's key, as ssh prints it without the prefix, pinned
    /// when a person pressed Trust after a test showed it to them. A server that answers with any
    /// other key is refused: that is either a reinstalled server or somebody in the middle, and
    /// Meows cannot tell which.
    /// </summary>
    public string? HostKey { get; set; }
}

public static class ReachKinds
{
    public const string None = "none";
    public const string Folder = "folder";
    public const string Sftp = "sftp";
}

/// <summary>What a test of the server said, and for SFTP the key it answered with.</summary>
public sealed record ReachTest(bool Ok, string Message, string? Fingerprint = null, bool FingerprintIsNew = false);

/// <summary>Turning a plugin's relative path into pieces, and refusing the ones that climb out.</summary>
public static class ReachPaths
{
    /// <summary>
    /// The path's folders in order, or null when it is not a plain relative path: rooted, a drive,
    /// a <c>..</c> or a <c>.</c> anywhere, or characters no file system takes. Both slashes are
    /// read as separators; empty pieces from doubled slashes are dropped.
    /// </summary>
    public static IReadOnlyList<string>? Split(string remotePath)
    {
        if (string.IsNullOrWhiteSpace(remotePath))
            return [];
        if (remotePath.StartsWith('/') || remotePath.StartsWith('\\') || remotePath.Contains(':'))
            return null;

        var parts = remotePath.Split(['/', '\\'], StringSplitOptions.RemoveEmptyEntries);
        foreach (var part in parts)
        {
            if (part is "." or ".." || part.Trim().Length == 0 || part.IndexOfAny(['<', '>', '"', '|', '?', '*', '\0']) >= 0)
                return null;
        }
        return parts;
    }
}

/// <summary>
/// The shell's end of <see cref="IMeowsReach"/>: the server from the Settings tab, reached as a
/// folder or over SFTP. Plugins name a place under the root and hand over a folder; which of the
/// two it is, and the credentials for the second, stay here.
/// </summary>
public sealed class ShellReach : IMeowsReach
{
    public const string KeySecret = "reach.key";
    public const string PassphraseSecret = "reach.passphrase";

    /// <summary>What a file is called while it is on its way, so a cut-off copy is never mistaken for the real one.</summary>
    public const string PartSuffix = ".meows-part";

    private readonly Func<ReachSettings> _settings;
    private readonly IMeowsSecrets _secrets;
    private readonly Action<string, LogLevel> _log;

    public ShellReach(Func<ReachSettings> settings, IMeowsSecrets secrets, Action<string, LogLevel> log)
    {
        _settings = settings;
        _secrets = secrets;
        _log = log;
    }

    private static IMeowsText Text => MeowsText.Current;

    public bool IsSet => Missing() is null;

    /// <summary>Why the server cannot be used yet, as a sentence, or null when it can.</summary>
    public string? Missing()
    {
        var s = _settings();
        switch (s.Kind)
        {
            case ReachKinds.Folder:
                return string.IsNullOrWhiteSpace(s.Folder) ? Text["reach.missing.folder"] : null;
            case ReachKinds.Sftp:
                if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.User) || string.IsNullOrWhiteSpace(s.RemoteRoot))
                    return Text["reach.missing.address"];
                if (!_secrets.Has(KeySecret))
                    return Text["reach.missing.key"];
                if (string.IsNullOrWhiteSpace(s.HostKey))
                    return Text["reach.missing.trust"];
                return null;
            default:
                return Text["reach.missing.none"];
        }
    }

    public string? Where
    {
        get
        {
            var s = _settings();
            return s.Kind switch
            {
                ReachKinds.Folder when !string.IsNullOrWhiteSpace(s.Folder) => s.Folder,
                ReachKinds.Sftp when !string.IsNullOrWhiteSpace(s.Host) =>
                    $"sftp://{s.User}@{s.Host}{(s.Port == 22 ? "" : ":" + s.Port)}{RemoteRootOf(s)}",
                _ => null,
            };
        }
    }

    private static string RemoteRootOf(ReachSettings s)
    {
        var root = (s.RemoteRoot ?? "").Trim().Replace('\\', '/');
        return root.StartsWith('/') ? root.TrimEnd('/') : "/" + root.TrimEnd('/');
    }

    public async Task<ReachResult> CopyFolder(string localFolder, string remotePath,
        IProgress<ReachProgress>? progress = null, CancellationToken token = default)
    {
        var shown = remotePath;
        if (Missing() is { } missing)
            return new ReachResult(false, 0, 0, shown, missing);
        if (!Directory.Exists(localFolder))
            return new ReachResult(false, 0, 0, shown, Text.Format("reach.error.nolocal", localFolder));
        if (ReachPaths.Split(remotePath) is not { } parts)
            return new ReachResult(false, 0, 0, shown, Text.Format("reach.error.path", remotePath));

        var files = Directory.EnumerateFiles(localFolder, "*", SearchOption.AllDirectories)
            .Select(f => (Full: f, Relative: Path.GetRelativePath(localFolder, f)))
            .ToList();

        var settings = _settings();
        var result = settings.Kind == ReachKinds.Folder
            ? await Task.Run(() => ToFolder(settings, parts, files, progress, token), token)
            : await Task.Run(() => ToSftp(settings, parts, files, progress, token), token);

        _log(result.Ok
                ? $"Copied {result.Files} file(s), {result.Bytes} bytes, from {localFolder} to {result.Where}."
                : $"Could not copy {localFolder} to {result.Where}: {result.Error}",
            result.Ok ? LogLevel.Info : LogLevel.Warning);
        return result;
    }

    // ---- a folder: a share, a mapped drive, anything mounted --------------------------------------

    private static ReachResult ToFolder(ReachSettings settings, IReadOnlyList<string> parts,
        List<(string Full, string Relative)> files, IProgress<ReachProgress>? progress, CancellationToken token)
    {
        var root = settings.Folder!.Trim();
        var target = Path.Combine([root, .. parts]);
        if (!Directory.Exists(root))
            return new ReachResult(false, 0, 0, target, Text.Format("reach.error.noroot", root));

        var done = 0;
        long bytes = 0;
        foreach (var (full, relative) in files)
        {
            token.ThrowIfCancellationRequested();
            var destination = Path.Combine(target, relative);
            var part = destination + PartSuffix;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
                File.Copy(full, part, overwrite: true);
                var length = new FileInfo(full).Length;
                if (new FileInfo(part).Length != length)
                    throw new IOException(Text.Format("reach.error.size", relative));
                File.Move(part, destination, overwrite: true);
                bytes += length;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                TryDelete(part);
                return new ReachResult(false, done, bytes, target, $"{relative}: {ex.Message}");
            }

            done++;
            progress?.Report(new ReachProgress(done, files.Count, relative));
        }

        return new ReachResult(true, done, bytes, target);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (Exception)
        {
        }
    }

    // ---- SFTP ----------------------------------------------------------------------------------

    private ReachResult ToSftp(ReachSettings settings, IReadOnlyList<string> parts,
        List<(string Full, string Relative)> files, IProgress<ReachProgress>? progress, CancellationToken token)
    {
        var target = RemoteRootOf(settings) + (parts.Count == 0 ? "" : "/" + string.Join('/', parts));
        var shown = $"sftp://{settings.Host}{target}";

        SftpClient client;
        try
        {
            client = Connect(settings, out _, trustAnything: false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return new ReachResult(false, 0, 0, shown, ex.Message);
        }

        using (client)
        {
            var done = 0;
            long bytes = 0;
            try
            {
                if (!client.Exists(RemoteRootOf(settings)))
                    return new ReachResult(false, 0, 0, shown, Text.Format("reach.error.noroot", RemoteRootOf(settings)));

                var made = new HashSet<string>(StringComparer.Ordinal);
                foreach (var (full, relative) in files)
                {
                    token.ThrowIfCancellationRequested();
                    var destination = target + "/" + relative.Replace('\\', '/');
                    var part = destination + PartSuffix;

                    MakeFolders(client, RemoteRootOf(settings), destination[..destination.LastIndexOf('/')], made);

                    var length = new FileInfo(full).Length;
                    using (var stream = File.OpenRead(full))
                        client.UploadFile(stream, part, canOverride: true);
                    if (client.GetAttributes(part).Size != length)
                    {
                        TryDelete(client, part);
                        return new ReachResult(false, done, bytes, shown, Text.Format("reach.error.size", relative));
                    }
                    PutInPlace(client, part, destination);

                    bytes += length;
                    done++;
                    progress?.Report(new ReachProgress(done, files.Count, relative));
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                return new ReachResult(false, done, bytes, shown, ex.Message);
            }

            return new ReachResult(true, done, bytes, shown);
        }
    }

    /// <summary>
    /// Every folder between the root and <paramref name="folder"/>, made where it is missing.
    /// The root itself must already be there: a mistyped root should be an error, not a new
    /// folder tree somewhere nobody meant.
    /// </summary>
    private static void MakeFolders(SftpClient client, string root, string folder, HashSet<string> made)
    {
        if (made.Contains(folder) || folder.Length <= root.Length)
            return;

        var path = root;
        foreach (var piece in folder[root.Length..].Split('/', StringSplitOptions.RemoveEmptyEntries))
        {
            path += "/" + piece;
            if (made.Contains(path))
                continue;
            if (!client.Exists(path))
                client.CreateDirectory(path);
            made.Add(path);
        }
    }

    /// <summary>
    /// The finished upload under its real name. OpenSSH's rename replaces in one step; a server
    /// without that extension gets the old file removed first, which leaves a moment with no file
    /// rather than a moment with half of one.
    /// </summary>
    private static void PutInPlace(SftpClient client, string part, string destination)
    {
        try
        {
            client.RenameFile(part, destination, isPosix: true);
        }
        catch (Exception)
        {
            if (client.Exists(destination))
                client.DeleteFile(destination);
            client.RenameFile(part, destination);
        }
    }

    private static void TryDelete(SftpClient client, string path)
    {
        try
        {
            client.DeleteFile(path);
        }
        catch (Exception)
        {
        }
    }

    private SftpClient Connect(ReachSettings settings, out string? seen, bool trustAnything)
    {
        var key = _secrets.Get(KeySecret) ?? throw new InvalidOperationException(Text["reach.missing.key"]);
        var passphrase = _secrets.Get(PassphraseSecret);

        PrivateKeyFile keyFile;
        try
        {
            using var stream = new MemoryStream(Encoding.UTF8.GetBytes(key));
            keyFile = new PrivateKeyFile(stream, string.IsNullOrEmpty(passphrase) ? null : passphrase);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(Text.Format("reach.error.key", ex.Message), ex);
        }

        var info = new ConnectionInfo(settings.Host!.Trim(), settings.Port, settings.User!.Trim(),
            new PrivateKeyAuthenticationMethod(settings.User!.Trim(), keyFile))
        {
            Timeout = TimeSpan.FromSeconds(20),
        };

        var client = new SftpClient(info);
        string? fingerprint = null;
        var pinned = settings.HostKey;
        client.HostKeyReceived += (_, e) =>
        {
            fingerprint = e.FingerPrintSHA256;
            e.CanTrust = trustAnything || string.Equals(pinned, fingerprint, StringComparison.Ordinal);
        };

        try
        {
            client.Connect();
        }
        catch (Exception ex)
        {
            client.Dispose();
            seen = fingerprint;
            if (!trustAnything && fingerprint is not null && !string.Equals(pinned, fingerprint, StringComparison.Ordinal))
                throw new InvalidOperationException(Text.Format("reach.error.hostkey", fingerprint, pinned ?? ""), ex);
            throw new InvalidOperationException(Text.Format("reach.error.connect", settings.Host, ex.Message), ex);
        }

        seen = fingerprint;
        return client;
    }

    /// <summary>
    /// The Settings tab's Test. A folder must be there and take a file; SFTP must connect with the
    /// key, and the key the server answers with is handed back so a person can compare it and
    /// press Trust. A server whose key differs from the pinned one fails the test and says both.
    /// </summary>
    public Task<ReachTest> Test(CancellationToken token = default) => Task.Run(() =>
    {
        var s = _settings();
        switch (s.Kind)
        {
            case ReachKinds.Folder:
            {
                if (string.IsNullOrWhiteSpace(s.Folder))
                    return new ReachTest(false, Text["reach.missing.folder"]);
                var root = s.Folder.Trim();
                if (!Directory.Exists(root))
                    return new ReachTest(false, Text.Format("reach.error.noroot", root));
                var probe = Path.Combine(root, ".meows-reach-test" + PartSuffix);
                try
                {
                    File.WriteAllText(probe, "meows");
                    File.Delete(probe);
                }
                catch (Exception ex)
                {
                    return new ReachTest(false, Text.Format("reach.test.readonly", root, ex.Message));
                }
                return new ReachTest(true, Text.Format("reach.test.folder", root));
            }

            case ReachKinds.Sftp:
            {
                if (string.IsNullOrWhiteSpace(s.Host) || string.IsNullOrWhiteSpace(s.User) || string.IsNullOrWhiteSpace(s.RemoteRoot))
                    return new ReachTest(false, Text["reach.missing.address"]);
                if (!_secrets.Has(KeySecret))
                    return new ReachTest(false, Text["reach.missing.key"]);

                string? seen;
                SftpClient client;
                try
                {
                    // Anything is let in for the test itself: the point is to see the key. It is
                    // compared below, and nothing is copied on a test.
                    client = Connect(s, out seen, trustAnything: true);
                }
                catch (Exception ex)
                {
                    return new ReachTest(false, ex.Message);
                }

                using (client)
                {
                    var isNew = !string.Equals(seen, s.HostKey, StringComparison.Ordinal);
                    if (isNew && !string.IsNullOrWhiteSpace(s.HostKey))
                        return new ReachTest(false, Text.Format("reach.error.hostkey", seen ?? "", s.HostKey), seen, FingerprintIsNew: true);

                    var root = RemoteRootOf(s);
                    bool there;
                    try
                    {
                        there = client.Exists(root) && client.GetAttributes(root).IsDirectory;
                    }
                    catch (Exception ex)
                    {
                        return new ReachTest(false, ex.Message, seen, isNew);
                    }
                    if (!there)
                        return new ReachTest(false, Text.Format("reach.error.noroot", root), seen, isNew);

                    return isNew
                        ? new ReachTest(true, Text.Format("reach.test.newkey", s.Host, seen ?? ""), seen, FingerprintIsNew: true)
                        : new ReachTest(true, Text.Format("reach.test.sftp", s.Host, root), seen);
                }
            }

            default:
                return new ReachTest(false, Text["reach.missing.none"]);
        }
    }, token);
}
