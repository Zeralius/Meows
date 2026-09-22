using System.Collections.ObjectModel;
using Meows.Plugins.Abstractions;

namespace Nudge;

/// <summary>
/// Hands a folder to another plugin and takes folders and files from any of them. Both halves
/// of a handoff in one small tab: pick a folder, nudge Chonk or Purrge with it and hear how it
/// went; and anything Chonk or Kibble sends this way is listed as it arrives.
///
/// The shape to copy: <see cref="IHandoffTarget.Accepts"/> says no before anything is
/// dropped, <see cref="IHandoffTarget.Receive"/> runs on the UI thread with the tab already in
/// front, and a sender that wants to know how it went sets <see cref="Handoff.Reply"/>.
/// </summary>
public sealed class NudgeViewModel : ObservableObject, IDisposable, IHandoffTarget
{
    private readonly IMeowsHost _host;
    private readonly LanguageWatch _language;
    private string? _folder;
    private string? _reply;

    public NudgeViewModel(IMeowsHost host)
    {
        _host = host;
        _language = new LanguageWatch(OnEverythingChanged);
        PickFolderCommand = new RelayCommand(() => _ = PickFolderAsync());
        NudgeChonkCommand = new RelayCommand(() => Nudge(KnownPlugins.Chonk), () => CanNudge(KnownPlugins.Chonk));
        NudgePurrgeCommand = new RelayCommand(() => Nudge(KnownPlugins.Purrge), () => CanNudge(KnownPlugins.Purrge));
    }

    public RelayCommand PickFolderCommand { get; }

    public RelayCommand NudgeChonkCommand { get; }

    public RelayCommand NudgePurrgeCommand { get; }

    /// <summary>What other plugins sent here, newest first: one line each.</summary>
    public ObservableCollection<string> Arrived { get; } = [];

    public bool HasArrived => Arrived.Count > 0;

    public string FolderText => _folder ?? _host.Text["nudge.folder.none"];

    public bool HasFolder => _folder is not null;

    /// <summary>What the receiver said, or what the shell said when it could not deliver.</summary>
    public string ReplyText => _reply ?? "";

    public bool HasReply => _reply is not null;

    private async Task PickFolderAsync()
    {
        // The shell's dialog, over its own window, from a view model with no TopLevel in hand.
        // Null when cancelled and also when there is no window, which changes nothing.
        var picked = await _host.Pick.Folder(new PickOptions { Title = _host.Text["nudge.pick"] });
        if (picked is null)
            return;
        _folder = picked;
        _reply = null;
        OnPropertyChanged(nameof(FolderText));
        OnPropertyChanged(nameof(HasFolder));
        OnPropertyChanged(nameof(ReplyText));
        OnPropertyChanged(nameof(HasReply));
        NudgeChonkCommand.RaiseCanExecuteChanged();
        NudgePurrgeCommand.RaiseCanExecuteChanged();
    }

    /// <summary>Installed and could be opened; not whether it would take this particular handoff.</summary>
    private bool CanNudge(string pluginId) => _folder is not null && _host.Handoff.CanReach(pluginId);

    private void Nudge(string pluginId)
    {
        if (_folder is not { } folder)
            return;

        // Reply is set by the sender and called once by the receiver, on the UI thread, when
        // the work is done: "3 sets, 12 copies". A receiver that never answers is allowed.
        var handoff = Handoff.Folder(folder, _host.Text["nudge.note"]) with
        {
            Reply = outcome => SetReply(_host.Text.Format("nudge.reply", pluginId, outcome)),
        };

        if (_host.Handoff.Send(pluginId, handoff))
        {
            _host.Log($"Nudge: handed {folder} to {pluginId}.");
            SetReply(_host.Text.Format("nudge.sent", pluginId));
        }
        else
        {
            // Not there, would not open, or does not take folders. The sender is told rather
            // than left wondering.
            SetReply(_host.Text.Format("nudge.refused", pluginId));
        }
    }

    private void SetReply(string reply)
    {
        _reply = reply;
        OnPropertyChanged(nameof(ReplyText));
        OnPropertyChanged(nameof(HasReply));
    }

    // ---- The receiving end ----------------------------------------------------------------

    /// <summary>Asked before anything is sent, so a sender can be told no rather than dropped.</summary>
    public bool Accepts(Handoff handoff) => handoff.Verb is HandoffVerbs.Folder or HandoffVerbs.Files;

    /// <summary>On the UI thread, after the tab has been brought to the front.</summary>
    public void Receive(Handoff handoff)
    {
        var text = _host.Text;
        var what = handoff.Verb == HandoffVerbs.Folder
            ? text.Format("nudge.arrived.folder", handoff.Paths.FirstOrDefault() ?? "")
            : text.Format("nudge.arrived.files", handoff.Paths.Count);
        if (handoff.Note is { Length: > 0 } note)
            what += $" ({note})";

        Arrived.Insert(0, $"{DateTime.Now:HH:mm}  {what}");
        OnPropertyChanged(nameof(HasArrived));
        _host.Log($"Nudge: received {handoff.Verb} with {handoff.Paths.Count} path(s).");

        // Safe when nobody asked, and safe to call twice.
        handoff.Answer(text.Format("nudge.answer", handoff.Paths.Count));
    }

    public void Dispose() => _language.Dispose();
}
