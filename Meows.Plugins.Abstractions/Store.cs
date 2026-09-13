namespace Meows.Plugins.Abstractions;

/// <summary>One thing a plugin did, written down.</summary>
public sealed record StoredEvent(
    long Id,
    DateTime At,
    string Plugin,
    string Kind,
    string Subject,
    string? Detail,
    IReadOnlyDictionary<string, string> Data);

/// <summary>
/// The shared store: what happened, kept past the window closing and past the plugin that did it.
///
/// Every plugin had its own settings.json and nothing else, so nothing anywhere recorded what
/// actually happened: what Kibble sent where and when, what Purrge removed, what Scruff posted.
/// This is that record. It is one SQLite file under Meows' own folder, the shell owns the schema
/// and versions it from the first write, and a plugin sees its own events and facts plus the one
/// table that is deliberately shared, the hashes anything has seen.
///
/// It is a journal and a notebook, not a database for a plugin to design tables in. A plugin
/// that needs its own tables should keep its own file in its data folder.
/// </summary>
public interface IMeowsStore
{
    /// <summary>
    /// Writes one event. <paramref name="kind"/> is a short word you choose and will filter on,
    /// "sent", "recycled", "posted"; <paramref name="subject"/> is what it happened to, usually a
    /// path; <paramref name="detail"/> is for a person; <paramref name="data"/> is for code.
    /// </summary>
    void Record(string kind, string subject, string? detail = null, IReadOnlyDictionary<string, string>? data = null);

    /// <summary>This plugin's events, newest first, optionally one kind of them.</summary>
    IReadOnlyList<StoredEvent> Recent(int limit = 100, string? kind = null);

    /// <summary>This plugin's events whose subject or detail contains the text, newest first.</summary>
    IReadOnlyList<StoredEvent> Search(string text, int limit = 100);

    /// <summary>A small durable fact, scoped to this plugin. Not settings: things learned rather than chosen.</summary>
    string? Get(string key);

    void Set(string key, string value);

    void Remove(string key);

    /// <summary>
    /// Says that this content has been seen, by hash. Shared across plugins on purpose: Kibble
    /// queuing a picture and Birdwatch saving one are the same picture if the bytes agree.
    /// </summary>
    void MarkSeen(string hash, string? note = null);

    /// <summary>Who saw it and when, or null.</summary>
    SeenRecord? Seen(string hash);
}

/// <summary>Where a hash was first seen.</summary>
public sealed record SeenRecord(string Hash, string Plugin, DateTime At, string? Note);

/// <summary>What a shell built against an older contract answers. Nothing is kept.</summary>
public sealed class NoStore : IMeowsStore
{
    public static NoStore Instance { get; } = new();

    public void Record(string kind, string subject, string? detail = null, IReadOnlyDictionary<string, string>? data = null)
    {
    }

    public IReadOnlyList<StoredEvent> Recent(int limit = 100, string? kind = null) => [];

    public IReadOnlyList<StoredEvent> Search(string text, int limit = 100) => [];

    public string? Get(string key) => null;

    public void Set(string key, string value)
    {
    }

    public void Remove(string key)
    {
    }

    public void MarkSeen(string hash, string? note = null)
    {
    }

    public SeenRecord? Seen(string hash) => null;
}
