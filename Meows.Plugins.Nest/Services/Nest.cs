using System.Security.Cryptography;
using Meows.Disk;

namespace Meows.Plugins.Nest.Services;

/// <summary>
/// A place that holds something that cannot be downloaded again. Known places are a starting
/// point, never the authority: games move their saves, and the list is out of date the day it is
/// written, which is why every one can be switched off and any folder can be added.
/// </summary>
/// <param name="Key">Stable, and the name of its folder in the copy.</param>
/// <param name="NameKey">A catalogue key for a known place; for one added by hand, the folder's own name.</param>
public sealed record NestPlace(string Key, string NameKey, string Path, bool IsKnown);

/// <summary>What a place holds now.</summary>
public sealed record NestMeasure(long Bytes, int Files, DateTime? NewestUtc);

/// <summary>A place against its copy: how many files are not in the copy as they are here.</summary>
public sealed record NestStanding(int Changed, long ChangedBytes, int Files, DateTime? CopyNewestUtc)
{
    public bool HasCopy => CopyNewestUtc is not null;

    public bool UpToDate => HasCopy && Changed == 0;
}

public sealed record NestCopyOutcome(int Copied, long Bytes, IReadOnlyList<string> Failed);

public static class NestPlaces
{
    /// <summary>
    /// Where the irreplaceable usually is on Windows. Only the ones that exist on this machine
    /// are shown, and each can be switched off.
    /// </summary>
    public static IReadOnlyList<NestPlace> Known()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
        var places = new List<NestPlace>
        {
            new("saved-games", "nest.place.savedgames", System.IO.Path.Combine(profile, "Saved Games"), true),
            new("my-games", "nest.place.mygames", System.IO.Path.Combine(documents, "My Games"), true),
            new("locallow", "nest.place.locallow", System.IO.Path.Combine(profile, "AppData", "LocalLow"), true),
            new("minecraft", "nest.place.minecraft", System.IO.Path.Combine(roaming, ".minecraft", "saves"), true),
            new("wonderdraft", "nest.place.wonderdraft", System.IO.Path.Combine(roaming, "Wonderdraft"), true),
            new("dungeondraft", "nest.place.dungeondraft", System.IO.Path.Combine(roaming, "Dungeondraft"), true),
            new("adobe", "nest.place.adobe", System.IO.Path.Combine(documents, "Adobe"), true),
            new("ssh", "nest.place.ssh", System.IO.Path.Combine(profile, ".ssh"), true),
        };

        // Steam's per-account folder: cloud saves for the games that have them, and local-only
        // configs and saves for the ones that do not.
        if (SteamLibrary.LibraryFolders() is [var client, ..])
            places.Add(new NestPlace("steam-userdata", "nest.place.steamuserdata", System.IO.Path.Combine(client, "userdata"), true));

        return places;
    }

    /// <summary>A place added by hand: keyed by its name and a short hash of its path, so two folders called Saves do not share a copy.</summary>
    public static NestPlace Added(string path)
    {
        var full = System.IO.Path.GetFullPath(path).TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar);
        var hash = Convert.ToHexString(SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(full.ToLowerInvariant())))[..8].ToLowerInvariant();
        var name = System.IO.Path.GetFileName(full);
        var safe = new string(name.Select(c => char.IsLetterOrDigit(c) || c is '-' or '_' or ' ' ? c : '_').ToArray()).Trim();
        return new NestPlace($"{(safe.Length == 0 ? "folder" : safe)}-{hash}", name, full, false);
    }

    /// <summary>What a place holds, walked without following links.</summary>
    public static NestMeasure Measure(string path, CancellationToken token = default)
    {
        long bytes = 0;
        var files = 0;
        DateTime? newest = null;
        foreach (var file in Files(path, token))
        {
            files++;
            bytes += file.Length;
            if (newest is null || file.LastWriteTimeUtc > newest)
                newest = file.LastWriteTimeUtc;
        }
        return new NestMeasure(bytes, files, newest);
    }

    /// <summary>
    /// Every file under a place with its path relative to it. Links are not followed, so a
    /// junction inside a save folder never pulls another drive into the copy.
    /// </summary>
    public static IEnumerable<FileInfo> Files(string root, CancellationToken token = default)
    {
        if (!Directory.Exists(root))
            yield break;
        var pending = new Stack<DirectoryInfo>();
        pending.Push(new DirectoryInfo(root));
        while (pending.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            var folder = pending.Pop();
            FileSystemInfo[] entries;
            try
            {
                entries = folder.GetFileSystemInfos();
            }
            catch (Exception)
            {
                continue;
            }
            foreach (var entry in entries)
            {
                if (entry.Attributes.HasFlag(FileAttributes.ReparsePoint))
                    continue;
                if (entry is DirectoryInfo child)
                    pending.Push(child);
                else if (entry is FileInfo file)
                    yield return file;
            }
        }
    }

    /// <summary>Where a place's copy lives under the copy folder.</summary>
    public static string CopyOf(NestPlace place, string copyRoot) => System.IO.Path.Combine(copyRoot, place.Key);

    /// <summary>
    /// A place held up against its copy. A file counts as copied when the copy has it with the
    /// same size and the same date, which the copy is given when it is made.
    /// </summary>
    public static NestStanding Standing(string path, string copy, CancellationToken token = default)
    {
        var changed = 0;
        long changedBytes = 0;
        var files = 0;
        foreach (var file in Files(path, token))
        {
            files++;
            var twin = new FileInfo(System.IO.Path.Combine(copy, System.IO.Path.GetRelativePath(path, file.FullName)));
            if (!twin.Exists || twin.Length != file.Length || twin.LastWriteTimeUtc != file.LastWriteTimeUtc)
            {
                changed++;
                changedBytes += file.Length;
            }
        }

        var copyNewest = Directory.Exists(copy) ? Measure(copy, token).NewestUtc ?? Directory.GetLastWriteTimeUtc(copy) : (DateTime?)null;
        return new NestStanding(changed, changedBytes, files, copyNewest);
    }

    /// <summary>
    /// Brings a place's copy up to date: every file that is new or changed is copied, hashed as it
    /// is read and read back afterwards. Nothing in the copy is ever deleted, so a save deleted
    /// here by mistake is still there.
    /// </summary>
    public static NestCopyOutcome CopyChanged(string path, string copy, IProgress<string>? progress, CancellationToken token)
    {
        var copied = 0;
        long bytes = 0;
        var failed = new List<string>();
        var buffer = new byte[1024 * 1024];

        foreach (var file in Files(path, token).ToList())
        {
            token.ThrowIfCancellationRequested();
            var relative = System.IO.Path.GetRelativePath(path, file.FullName);
            var target = new FileInfo(System.IO.Path.Combine(copy, relative));
            if (target.Exists && target.Length == file.Length && target.LastWriteTimeUtc == file.LastWriteTimeUtc)
                continue;

            var part = target.FullName + ".meows-part";
            try
            {
                Directory.CreateDirectory(target.DirectoryName!);
                string hash;
                using (var input = new FileStream(file.FullName, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, buffer.Length))
                using (var output = new FileStream(part, FileMode.Create, FileAccess.Write, FileShare.None, buffer.Length))
                using (var sha = IncrementalHash.CreateHash(HashAlgorithmName.SHA256))
                {
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        output.Write(buffer, 0, read);
                        sha.AppendData(buffer, 0, read);
                    }
                    hash = Convert.ToHexString(sha.GetHashAndReset());
                }

                if (ContentHash.Full(part) != hash)
                {
                    File.Delete(part);
                    failed.Add(relative);
                    continue;
                }

                File.Move(part, target.FullName, overwrite: true);
                File.SetLastWriteTimeUtc(target.FullName, file.LastWriteTimeUtc);
                copied++;
                bytes += file.Length;
                progress?.Report(relative);
            }
            catch (OperationCanceledException)
            {
                TryDelete(part);
                throw;
            }
            catch (Exception)
            {
                // A save the game has open and locked is the usual one; it is named, and the
                // rest still go.
                TryDelete(part);
                failed.Add(relative);
            }
        }

        return new NestCopyOutcome(copied, bytes, failed);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception)
        {
        }
    }
}
