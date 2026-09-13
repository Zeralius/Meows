namespace Meows.Plugins.Abstractions;

/// <summary>
/// Something one plugin passes to another: a folder Chonk found that Purrge should look at, a
/// pick of files Kibble would like Scruff to clean. Paths, and a word for what is meant by them.
/// </summary>
public sealed record Handoff(string Verb, IReadOnlyList<string> Paths, string? Note = null)
{
    public static Handoff Folder(string path, string? note = null) => new(HandoffVerbs.Folder, [path], note);

    public static Handoff Files(IEnumerable<string> paths, string? note = null) => new(HandoffVerbs.Files, paths.ToList(), note);
}

/// <summary>
/// The verbs everyone agrees on. Two plugins may invent a third between themselves; these are
/// the ones a plugin can take without knowing who sent them.
/// </summary>
public static class HandoffVerbs
{
    /// <summary>One folder: look at it, scan it, sort it, whatever the receiver does with a folder.</summary>
    public const string Folder = "folder";

    /// <summary>One or more files: take them, clean them, queue them.</summary>
    public const string Files = "files";
}

/// <summary>
/// How a plugin hands work to another. The receiver is opened if it is installed but switched
/// off, its tab is brought to the front, and its view model is given the handoff.
/// </summary>
public interface IMeowsHandoff
{
    /// <summary>Whether that plugin is installed and could be opened. Not whether it would accept this particular handoff.</summary>
    bool CanReach(string pluginId);

    /// <summary>
    /// Sends it. False when the plugin is not there, would not open, or does not take handoffs
    /// of this shape. Call from the UI thread.
    /// </summary>
    bool Send(string pluginId, Handoff handoff);
}

/// <summary>
/// Implemented by a plugin's view model, or its view, to be on the receiving end. The shell asks
/// <see cref="Accepts"/> first so a sender can be told no rather than having its handoff dropped.
/// </summary>
public interface IHandoffTarget
{
    bool Accepts(Handoff handoff);

    /// <summary>On the UI thread, after the tab has been brought to the front.</summary>
    void Receive(Handoff handoff);
}

/// <summary>The well known plugin ids, so a sender does not spell one wrong.</summary>
public static class KnownPlugins
{
    public const string Purrge = "meows.purrge";
    public const string Kibble = "meows.kibble";
    public const string Scruff = "meows.scruff";
    public const string Chonk = "meows.chonk";
    public const string Portion = "meows.portion";
}
