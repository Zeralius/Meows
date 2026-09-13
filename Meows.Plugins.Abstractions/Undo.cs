namespace Meows.Plugins.Abstractions;

/// <summary>
/// Implemented by a plugin's view model so a line in the shell's History tab can be reversed
/// from there: Kibble put a file in a queue, the line says so, and *Put back* on the line does
/// what Undo on Kibble's tab would do. The shell asks <see cref="CanUndo"/> first, so the button
/// only appears where pressing it would do something.
/// </summary>
public interface IUndoTarget
{
    /// <summary>
    /// Whether this event, one of this plugin's own, can still be reversed. Answer from the
    /// event and a look at the disk, never by doing anything.
    /// </summary>
    bool CanUndo(StoredEvent stored);

    /// <summary>
    /// Reverses it, on the UI thread, after the plugin's tab has been brought to the front.
    /// Returns null when it worked and a sentence for a person when it did not.
    /// </summary>
    string? Undo(StoredEvent stored);
}
