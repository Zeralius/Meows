using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using Meows.Disk;

namespace Meows.Plugins.Rehome.Services;

/// <summary>One folder carried over: where it came from, where it sits under the Rehome folder, and how the copy went.</summary>
public sealed class ManifestEntry
{
    public string Source { get; set; } = "";

    /// <summary>Relative to the Rehome folder: the source path with the drive letter turned into a folder.</summary>
    public string Stored { get; set; } = "";

    public string Kind { get; set; } = "";

    public string? Note { get; set; }

    public int Files { get; set; }

    public long Bytes { get; set; }

    public int Verified { get; set; }

    public int Skipped { get; set; }

    public List<string> Failed { get; set; } = [];

    public DateTime? Newest { get; set; }

    public bool Complete => Failed.Count == 0 && Verified == Files;
}

/// <summary>One of the things that are not folders: a file or an export, kept under extras\.</summary>
public sealed class ManifestExtra
{
    public string Kind { get; set; } = "";

    public string? Source { get; set; }

    public string Stored { get; set; } = "";

    public string? Note { get; set; }

    public bool Ok { get; set; }

    public string? Failure { get; set; }
}

/// <summary>What was carried, written beside it, so the way back needs nothing but the folder.</summary>
public sealed class Manifest
{
    public const string FileName = "rehome-manifest.json";

    public int Version { get; set; } = 1;

    public DateTime CreatedAt { get; set; }

    public string Machine { get; set; } = "";

    public string User { get; set; } = "";

    public string? WindowsVersion { get; set; }

    public List<string> WipedDrives { get; set; } = [];

    public List<ManifestEntry> Entries { get; set; } = [];

    public List<ManifestExtra> Extras { get; set; } = [];

    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    public void Save(string root) => File.WriteAllText(Path.Combine(root, FileName), JsonSerializer.Serialize(this, Json));

    public static Manifest? Load(string root)
    {
        var path = Path.Combine(root, FileName);
        if (!File.Exists(path))
            return null;
        try
        {
            return JsonSerializer.Deserialize<Manifest>(File.ReadAllText(path), Json);
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>"C:\Users\Dennis\AppData\Roaming\obs-studio" is stored as "C\Users\Dennis\AppData\Roaming\obs-studio".</summary>
    public static string StoredPathFor(string source)
    {
        var root = Path.GetPathRoot(source) ?? "";
        var letter = root.TrimEnd('\\', ':');
        var rest = source[root.Length..].TrimStart('\\');
        return letter.Length == 0 ? rest : Path.Combine(letter, rest);
    }
}

public sealed record CopyProgress(int Files, long Bytes, string Current);

public sealed record CopyOutcome(int Files, long Bytes, int Verified, int Skipped, IReadOnlyList<string> Failed, DateTime? Newest);

/// <summary>
/// The copy that carries a folder across and proves it arrived. Each file is hashed as it is
/// read and the copy is hashed after it is written; the two agreeing is what "verified" means.
/// Nothing is ever removed from the source: the wipe is the delete, and on the way back the
/// Rehome copy is the only one that survived it.
/// </summary>
public static class Carry
{
    /// <param name="pullCloudOnly">
    /// Read OneDrive's cloud-only placeholders too. Opening one makes OneDrive fetch it, which
    /// is what the person asked for when they ticked the option, and takes as long as the
    /// download does; with OneDrive not running the read fails and the file is listed as such.
    /// </param>
    public static CopyOutcome CopyTree(string source, string destination, Action<CopyProgress>? progress, CancellationToken token, Func<string, bool>? keepExisting = null, bool pullCloudOnly = false)
    {
        var files = 0;
        long bytes = 0;
        var verified = 0;
        var skipped = 0;
        var failed = new List<string>();
        DateTime? newest = null;

        var pending = new Stack<(DirectoryInfo From, string To)>();
        pending.Push((new DirectoryInfo(source), destination));

        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var (from, to) = pending.Pop();
            try
            {
                Directory.CreateDirectory(Long(to));
            }
            catch (Exception ex)
            {
                failed.Add($"{from.FullName}: {ex.Message}");
                continue;
            }
            if (!FolderWalk.CanRead(from))
            {
                failed.Add($"{from.FullName}: cannot be read");
                continue;
            }

            foreach (var file in FolderWalk.Files(from))
            {
                token.ThrowIfCancellationRequested();
                var target = Path.Combine(to, file.Name);
                try
                {
                    if (!pullCloudOnly && WipeList.Placeholder(file))
                    {
                        skipped++;
                        continue;
                    }
                    if (keepExisting is not null && File.Exists(Long(target)) && keepExisting(target))
                    {
                        skipped++;
                        continue;
                    }
                    var ok = CopyOne(file, target);
                    files++;
                    bytes += file.Length;
                    if (ok)
                        verified++;
                    else
                        failed.Add($"{file.FullName}: the copy does not match");
                    if (newest is null || file.LastWriteTime > newest)
                        newest = file.LastWriteTime;
                    progress?.Invoke(new CopyProgress(files, bytes, file.FullName));
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    files++;
                    failed.Add($"{file.FullName}: {ex.Message}");
                }
            }

            foreach (var child in FolderWalk.Into(from, skipSystemFolders: false))
                pending.Push((child, Path.Combine(to, child.Name)));
        }

        return new CopyOutcome(files, bytes, verified, skipped, failed, newest);
    }

    /// <summary>Copies one file, hashing the source on the way through, and reads the copy back to compare.</summary>
    private static bool CopyOne(FileInfo file, string target)
    {
        var buffer = new byte[1024 * 1024];
        string sourceHash;
        using (var input = new FileStream(Long(file.FullName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buffer.Length))
        using (var output = new FileStream(Long(target), FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length))
        using (var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
        {
            int read;
            while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
            {
                output.Write(buffer, 0, read);
                hash.AppendData(buffer, 0, read);
            }
            sourceHash = Convert.ToHexString(hash.GetHashAndReset());
        }

        try
        {
            File.SetLastWriteTime(Long(target), file.LastWriteTime);
            File.SetCreationTime(Long(target), file.CreationTime);
        }
        catch (Exception)
        {
            // The bytes matter more than the stamps.
        }

        var copyHash = HashOf(Long(target), buffer);
        return copyHash == sourceHash;
    }

    private static string? HashOf(string path, byte[] buffer)
    {
        try
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, buffer.Length);
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            int read;
            while ((read = stream.Read(buffer, 0, buffer.Length)) > 0)
                hash.AppendData(buffer, 0, read);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>A path past the old limit needs the prefix, or Windows refuses it; a short one must not have it, or Explorer does.</summary>
    public static string Long(string path)
    {
        if (path.Length < 240 || path.StartsWith(@"\\?\", StringComparison.Ordinal) || path.StartsWith(@"\\", StringComparison.Ordinal))
            return path;
        return @"\\?\" + path;
    }

    /// <summary>"Rehome 2026-09-21" under the drive picked, and a number on the end if today already has one.</summary>
    public static string RootFor(string destination, DateTime when)
    {
        var name = $"Rehome {when:yyyy-MM-dd}";
        var root = Path.Combine(destination, name);
        var n = 2;
        while (Directory.Exists(root) && Manifest.Load(root) is not null)
            root = Path.Combine(destination, $"{name} ({n++})");
        return root;
    }

    /// <summary>Whether the destination sits on one of the drives about to be wiped, which would make it no destination at all.</summary>
    public static bool OnWipedDrive(string destination, IReadOnlyList<string> wipedDrives)
    {
        var root = Path.GetPathRoot(destination)?.TrimEnd('\\');
        return root is not null && wipedDrives.Any(d => string.Equals(d.TrimEnd('\\'), root, StringComparison.OrdinalIgnoreCase));
    }
}
