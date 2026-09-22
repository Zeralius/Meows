using System.Collections.ObjectModel;
using Meows.Plugins.Abstractions;

namespace Yarn;

/// <summary>The threads themselves. Settings hold what was chosen; the journal holds what happened.</summary>
public sealed class YarnSettings
{
    public List<string> Threads { get; set; } = [];
}

/// <summary>
/// A list of things to remember, and a record of when each was kept. The list is settings; each
/// keep is written to the shared store as an event, so it shows on the History tab and on this
/// plugin's card, and a kept line there carries *Put back* for as long as the thread is still
/// on the list. Ctrl+K finds a thread whether the plugin is on or off.
///
/// The shape to copy: <see cref="IMeowsStore.Record"/> for what happened, with a kind you
/// will filter on; <see cref="ISearchable"/> answering from memory; <see cref="IUndoTarget"/>
/// answering <c>CanUndo</c> by looking and <c>Undo</c> by doing; and a handoff to itself as the
/// way a hit from <see cref="YarnPlugin.WhileOff"/> lands on the thing it found.
/// </summary>
public sealed class YarnViewModel : ObservableObject, IDisposable, ISearchable, IHandoffTarget, IUndoTarget, IGlanceable
{
    /// <summary>A verb only this plugin sends and takes: select the thread named in the note.</summary>
    public const string ShowVerb = "yarn.show";

    private const string Kept = "kept";

    private readonly IMeowsHost _host;
    private readonly YarnSettings _settings;
    private readonly LanguageWatch _language;
    private string _draft = "";
    private string? _selected;

    public YarnViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<YarnSettings>() ?? new();
        _language = new LanguageWatch(OnEverythingChanged);
        foreach (var thread in _settings.Threads)
            Threads.Add(thread);
        KeepCommand = new RelayCommand(Keep, () => Draft.Trim().Length > 0);
        ForgetCommand = new RelayCommand(() => Forget(Selected), () => Selected is not null);
        ReloadJournal();
    }

    public RelayCommand KeepCommand { get; }

    public RelayCommand ForgetCommand { get; }

    public ObservableCollection<string> Threads { get; } = [];

    /// <summary>This plugin's own journal, newest first, as the History tab would show it.</summary>
    public ObservableCollection<string> Journal { get; } = [];

    public bool IsEmpty => Threads.Count == 0;

    public string Draft
    {
        get => _draft;
        set
        {
            if (SetField(ref _draft, value))
                KeepCommand.RaiseCanExecuteChanged();
        }
    }

    public string? Selected
    {
        get => _selected;
        set
        {
            if (SetField(ref _selected, value))
                ForgetCommand.RaiseCanExecuteChanged();
        }
    }

    public string CountText => _host.Text.Format("yarn.count", Threads.Count);

    /// <summary>On the Home tab: how many threads, and the newest of them.</summary>
    public Glance? Glance() =>
        Threads.Count == 0 ? null : new Glance(_host.Text.Format("yarn.glance", Threads.Count, Threads[^1]));

    private void Keep()
    {
        var thread = Draft.Trim();
        if (thread.Length == 0)
            return;

        Threads.Add(thread);
        _settings.Threads = Threads.ToList();
        _host.SaveSettings(_settings);
        Draft = "";

        // The kind is a word to filter on later; the subject is what it happened to; the
        // detail is for a person. Data is for code and this plugin needs none.
        _host.Store.Record(Kept, thread, _host.Text["yarn.kept.detail"]);
        _host.Log($"Yarn: kept '{thread}'.");
        Changed();
    }

    private void Forget(string? thread)
    {
        if (thread is null || !Threads.Remove(thread))
            return;
        _settings.Threads = Threads.ToList();
        _host.SaveSettings(_settings);
        Selected = null;
        _host.Store.Record("forgot", thread, _host.Text["yarn.forgot.detail"]);
        Changed();
    }

    private void Changed()
    {
        ReloadJournal();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
    }

    private void ReloadJournal()
    {
        Journal.Clear();
        foreach (var stored in _host.Store.Recent(20))
            Journal.Add($"{stored.At:dd MMM HH:mm}  {stored.Kind}  {stored.Subject}");
    }

    // ---- Ctrl+K, while on -----------------------------------------------------------------

    /// <summary>On the UI thread on every keystroke: from memory, never from the disk.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        return Threads
            .Where(t => SearchWords.Match(words, t))
            .Take(limit)
            .Select(t => new SearchHit(t, _host.Text["yarn.hit.on"], () => Selected = t))
            .ToList();
    }

    // ---- The way in from Ctrl+K while off ---------------------------------------------------

    public bool Accepts(Handoff handoff) => handoff.Verb == ShowVerb;

    public void Receive(Handoff handoff) => Selected = Threads.FirstOrDefault(t => t == handoff.Note);

    // ---- Put back, from the History tab -----------------------------------------------------

    /// <summary>Only a keep can be reversed, and only while the thread is still on the list. Looks; does nothing.</summary>
    public bool CanUndo(StoredEvent stored) => stored.Kind == Kept && Threads.Contains(stored.Subject);

    /// <summary>Null when it worked; a sentence for a person when it did not.</summary>
    public string? Undo(StoredEvent stored)
    {
        if (!CanUndo(stored))
            return _host.Text["yarn.undo.gone"];
        Forget(stored.Subject);
        return null;
    }

    public void Dispose() => _language.Dispose();
}
