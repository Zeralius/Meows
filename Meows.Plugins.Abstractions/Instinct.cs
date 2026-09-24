namespace Meows.Plugins.Abstractions;

/// <summary>
/// Something a plugin can be asked to do with nobody at the window: check the queues, scan a
/// folder, put a date on the list. A rule on the shell's Rules tab asks for it when another
/// plugin records an event, which is how every plugin becomes an ingredient of the others
/// without either knowing the other exists.
///
/// Not a handoff verb. A handoff is a person pressing a button in one tab and landing in
/// another, so the receiver comes to the front and shows the thing. An action is asked for by a
/// rule, often while the window is hidden, and ends with one sentence for the History tab
/// rather than with anything on screen.
/// </summary>
/// <param name="Id">Stable forever: rules are saved by it. Short and lower case, "check", "scan".</param>
/// <param name="Label">What it does, as a verb phrase: "Check the queues". A key from your strings catalogue, or plain text.</param>
/// <param name="Description">One sentence under the label on the Rules tab, or null. A key or plain text, like the label.</param>
public sealed record PluginAction(string Id, string Label, string? Description = null);

/// <summary>
/// A kind of event a plugin writes to the store, so the Rules tab can offer it before the first
/// one has ever happened. The kind is the same short word passed to
/// <see cref="IMeowsStore.Record"/>; the label is how a person would say it: "saved a picture".
/// </summary>
/// <param name="Kind">Exactly as recorded, "saved", "recycled", "reading".</param>
/// <param name="Label">A past-tense phrase that follows the plugin's name: "saved a picture". A key or plain text.</param>
public sealed record RecordedKind(string Kind, string Label);

/// <summary>
/// What a rule asks of a plugin: which of its actions, and the event that set it off. The
/// subject of that event is usually a path, and is what most actions want to act on.
/// </summary>
/// <param name="Action">The <see cref="PluginAction.Id"/> asked for.</param>
/// <param name="Cause">The event another plugin recorded that the rule was waiting for.</param>
public sealed record ActionRequest(string Action, StoredEvent Cause)
{
    /// <summary>The cause's subject: a path, a name, whatever the recording plugin wrote.</summary>
    public string Subject => Cause.Subject;

    /// <summary>
    /// Where the thing is now. A plugin that moves something records where it came from as the
    /// subject and where it went as <c>destination</c> in the data, the way Kibble does for a file
    /// it queued; an action that wants the file wants the second. Otherwise the subject.
    /// </summary>
    public string Path => Cause.Data.TryGetValue(DestinationKey, out var moved) && moved.Length > 0 ? moved : Cause.Subject;

    /// <summary>The data key for where a moved thing went. See <see cref="Path"/>.</summary>
    public const string DestinationKey = "destination";
}

/// <summary>
/// Implemented by a plugin's view model to perform the actions its plugin declares in
/// <see cref="IMeowsPlugin.Actions"/>. The shell switches the plugin on first if it is off, but
/// does not bring its tab to the front: a rule is not a person looking.
///
/// Called on the UI thread. Do the work the way the tab would, through background work if it
/// is long, and finish with one sentence saying how it went: "3 sets, 12 copies", "on the list
/// for today". That sentence is what the History tab shows beside the rule. Throw to say it
/// could not be done; the message is shown the same way, as a failure.
///
/// Whatever the plugin records while it is performing is not offered to other rules. That is
/// the one-hop rule: a rule can start something, and that something cannot start another rule,
/// so no chain of them can run all night without anybody having asked for it.
/// </summary>
public interface IActionTarget
{
    /// <summary>
    /// Does it. <paramref name="token"/> is cancelled when Meows quits, when the plugin is
    /// switched off, or when the action has run for so long that the shell stopped waiting.
    /// </summary>
    Task<string> Perform(ActionRequest request, CancellationToken token);
}

/// <summary>
/// Thrown from <see cref="IActionTarget.Perform"/> for "not this time" rather than "broken": the
/// bot folder is not set, the file the rule named has gone. The shell writes the message as the
/// outcome without calling it an error, so History reads as a record rather than an alarm.
/// </summary>
public sealed class ActionDeclinedException(string message) : Exception(message);
