using Microsoft.Win32;

namespace Meows.Plugins.Trail.Services;

/// <summary>Where an entry came from. The order here is the order Windows joins them in.</summary>
public enum PathScope
{
    /// <summary>Set for everyone, in HKLM. Changing it needs rights Meows does not ask for.</summary>
    Machine,

    /// <summary>Set for whoever is logged in, in HKCU. This is the half that is safe to edit.</summary>
    User,

    /// <summary>
    /// On this process's PATH and in neither of the stored ones: something put it there after
    /// the process started, usually a shell profile or the thing that launched Meows.
    /// </summary>
    Process,
}

/// <summary>What is wrong with one entry, or nothing, which is the common case.</summary>
public enum PathFault
{
    None,

    /// <summary>The folder is not there. The commonest one, and always safe to say plainly.</summary>
    Missing,

    /// <summary>The same folder is earlier on the list. Only the first one has any effect.</summary>
    Duplicate,

    /// <summary>An empty entry, from a stray semicolon. Older Windows read it as "this folder".</summary>
    Empty,

    /// <summary>Quotes, a trailing slash, characters no path can hold: it will not resolve as written.</summary>
    Malformed,

    /// <summary>A variable in it that nothing expands to, so the entry resolves to nonsense.</summary>
    Unexpanded,
}

/// <param name="Raw">Exactly as stored, variables and all, which is what an edit has to preserve.</param>
/// <param name="Expanded">What Windows resolves it to, which is what actually gets searched.</param>
/// <param name="Position">Where it sits on the joined list, which decides which copy of a tool wins.</param>
public sealed record PathEntry(
    string Raw,
    string Expanded,
    PathScope Scope,
    int Position,
    PathFault Fault,
    string? Note = null)
{
    public bool IsTrouble => Fault is not PathFault.None;

    /// <summary>Only the user's half is Meows' to change. The machine's needs elevation.</summary>
    public bool CanEdit => Scope == PathScope.User;
}

/// <summary>Which entry a typed command would actually run out of, and what it is shadowing.</summary>
public sealed record Winner(string Command, string FoundAt, int Position, IReadOnlyList<string> Shadowed);

/// <summary>
/// PATH as it is stored and as it is used, which are two different strings.
///
/// The stored halves live in the registry as <c>REG_EXPAND_SZ</c>, holding things like
/// <c>%JAVA_HOME%\bin</c>. Reading them through <see cref="Environment.GetEnvironmentVariable(string,EnvironmentVariableTarget)"/>
/// expands those on the way out, and writing the result back would bake today's expansion into
/// the registry for good. So everything here reads raw, with
/// <see cref="RegistryValueOptions.DoNotExpandEnvironmentNames"/>, and expands separately for
/// the sake of looking things up.
/// </summary>
public static class PathRead
{
    private const string MachineKey = @"SYSTEM\CurrentControlSet\Control\Session Manager\Environment";
    private const string UserKey = "Environment";

    /// <summary>The raw stored value for one scope, or null when it cannot be read.</summary>
    public static string? Stored(PathScope scope, string name = "Path")
    {
        if (!OperatingSystem.IsWindows() || scope == PathScope.Process)
            return null;

        try
        {
            using var key = scope == PathScope.Machine
                ? Registry.LocalMachine.OpenSubKey(MachineKey)
                : Registry.CurrentUser.OpenSubKey(UserKey);

            return key?.GetValue(name, null, RegistryValueOptions.DoNotExpandEnvironmentNames) as string;
        }
        catch (Exception)
        {
            return null;
        }
    }

    /// <summary>
    /// Splits a stored PATH the way Windows does: on semicolons, keeping empties, since an empty
    /// entry is a real thing that used to mean the current folder and is worth reporting rather
    /// than quietly dropping.
    /// </summary>
    public static IReadOnlyList<string> Split(string? value) =>
        string.IsNullOrEmpty(value) ? [] : value.Split(';');

    /// <summary>
    /// Expands <c>%NAME%</c> against the environment this process has. A variable nothing
    /// defines is left as written, which is how <see cref="PathFault.Unexpanded"/> is spotted
    /// later: the expanded form still has percent signs in it.
    /// </summary>
    public static string Expand(string raw)
    {
        try
        {
            return Environment.ExpandEnvironmentVariables(raw);
        }
        catch (Exception)
        {
            return raw;
        }
    }

    /// <summary>
    /// Every entry from every scope, in the order Windows searches them: the machine's first,
    /// then the user's, then anything only this process has. The position is that order, and it
    /// is the whole of why one copy of a tool wins over another.
    /// </summary>
    public static IReadOnlyList<PathEntry> All() =>
        From(Stored(PathScope.Machine), Stored(PathScope.User), Environment.GetEnvironmentVariable("PATH"));

    /// <summary>
    /// The same, from three strings rather than from this machine. Everything that can be
    /// judged wrongly is judged in here, so it can be judged against a PATH written for the
    /// purpose rather than whatever this computer happens to have on it today.
    /// </summary>
    public static IReadOnlyList<PathEntry> From(string? machine, string? user, string? process)
    {
        var entries = new List<PathEntry>();
        var seen = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

        void Add(string raw, PathScope scope)
        {
            var expanded = Expand(raw);
            var position = entries.Count;
            var (fault, note) = Judge(raw, expanded, seen);

            if (fault is not PathFault.Duplicate && fault is not PathFault.Empty)
                seen.TryAdd(Key(expanded), position);

            entries.Add(new PathEntry(raw, expanded, scope, position, fault, note));
        }

        foreach (var raw in Split(machine))
            Add(raw, PathScope.Machine);

        foreach (var raw in Split(user))
            Add(raw, PathScope.User);

        // What this process actually got, minus what the two stored halves already explain.
        // Anything left is something that was put there after the process started, which is
        // worth showing precisely because it will not be there next time.
        var stored = entries.Select(e => Key(e.Expanded)).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in Split(process))
        {
            if (raw.Length == 0 || stored.Contains(Key(Expand(raw))))
                continue;
            Add(raw, PathScope.Process);
        }

        return entries;
    }

    /// <summary>
    /// The one judgement in here. Everything it can say is a fact about the string or the disk;
    /// nothing is an opinion about whether an entry ought to be there.
    /// </summary>
    private static (PathFault Fault, string? Note) Judge(string raw, string expanded, Dictionary<string, int> seen)
    {
        if (raw.Trim().Length == 0)
            return (PathFault.Empty, null);

        if (expanded.Contains('%'))
            return (PathFault.Unexpanded, null);

        if (raw.Contains('"'))
            return (PathFault.Malformed, null);

        if (raw.IndexOfAny(Path.GetInvalidPathChars()) >= 0)
            return (PathFault.Malformed, null);

        if (seen.TryGetValue(Key(expanded), out var first))
            return (PathFault.Duplicate, first.ToString());

        try
        {
            if (!Directory.Exists(expanded))
                return (PathFault.Missing, null);
        }
        catch (Exception)
        {
            return (PathFault.Malformed, null);
        }

        return (PathFault.None, null);
    }

    /// <summary>
    /// What makes two entries the same folder. A trailing slash and a different case are the
    /// two ways the same folder gets on the list twice without looking like it.
    /// </summary>
    private static string Key(string expanded)
    {
        var trimmed = expanded.Trim().TrimEnd('\\', '/');
        try
        {
            return Path.GetFullPath(trimmed).TrimEnd('\\', '/');
        }
        catch (Exception)
        {
            return trimmed;
        }
    }

    /// <summary>
    /// The extensions Windows tries when a command is typed without one, in order. This is the
    /// other half of which copy wins: <c>foo.cmd</c> earlier on the list beats <c>foo.exe</c>
    /// later, and nothing about the folder order alone would tell you that.
    /// </summary>
    public static IReadOnlyList<string> Extensions()
    {
        var raw = Environment.GetEnvironmentVariable("PATHEXT");
        var list = string.IsNullOrWhiteSpace(raw)
            ? [".COM", ".EXE", ".BAT", ".CMD"]
            : raw.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return list.Select(e => e.StartsWith('.') ? e : "." + e).ToList();
    }

    /// <summary>
    /// Where a typed command would actually come from, and every other copy of it further down
    /// that will therefore never run. This is the question that costs the afternoon: two SDKs
    /// on the list and no way to see which one wins short of running it.
    /// </summary>
    public static Winner? Resolve(string command, IReadOnlyList<PathEntry> entries)
    {
        command = command.Trim().Trim('"');
        if (command.Length == 0 || command.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            return null;

        var extensions = Path.HasExtension(command) ? [""] : Extensions();
        var hits = new List<(string Found, int Position)>();

        foreach (var entry in entries.Where(e => e.Fault is PathFault.None or PathFault.Duplicate))
        {
            foreach (var extension in extensions)
            {
                string? candidate;
                try
                {
                    // Asked of the folder rather than built from PATHEXT, so the answer carries
                    // the name as it is spelled on disk. PATHEXT is upper case, and a path
                    // printed as dotnet.EXE when the file is dotnet.exe is a path somebody will
                    // paste somewhere it does not work.
                    candidate = new DirectoryInfo(entry.Expanded)
                        .GetFiles(command + extension)
                        .FirstOrDefault()?.FullName;
                    if (candidate is null)
                        continue;
                }
                catch (Exception)
                {
                    continue;
                }

                hits.Add((candidate, entry.Position));
                break; // The first extension that matches in this folder is the one it would run.
            }
        }

        if (hits.Count == 0)
            return null;

        var winner = hits[0];
        return new Winner(command, winner.Found, winner.Position, hits.Skip(1).Select(h => h.Found).ToList());
    }

    /// <summary>
    /// Joins entries back into a stored value. Raw, never expanded, so <c>%JAVA_HOME%\bin</c>
    /// goes back as it came and keeps following the variable it was written to follow.
    /// </summary>
    public static string Join(IEnumerable<PathEntry> entries) =>
        string.Join(';', entries.Select(e => e.Raw));
}
