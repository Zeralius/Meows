using Meows.Plugins.Abstractions;

namespace MyPlugin;

public sealed class MyPluginViewModel : ObservableObject, IDisposable, IGlanceable, ISearchable
{
    private readonly IMeowsHost _host;

    /// <summary>
    /// Text worked out here rather than bound with {m:Tr} has to be read again when the language
    /// changes. Without this an open tab keeps whatever it read when it opened.
    /// </summary>
    private readonly LanguageWatch _language;

    private string? _status;

    public MyPluginViewModel(IMeowsHost host)
    {
        _host = host;
        SayHelloCommand = new RelayCommand(SayHello);
        _language = new LanguageWatch(OnEverythingChanged);
    }

    public RelayCommand SayHelloCommand { get; }

    /// <summary>
    /// Null until something happens, so the opening line follows the language. Once there is
    /// real news it stays as it was said: re-translating a past event would be rewriting it.
    /// </summary>
    public string Status
    {
        get => _status ?? _host.Text.Format("PLUGIN-ID.where", _host.DataDirectory);
        private set => SetField(ref _status, value);
    }

    private void SayHello()
    {
        // Everything the host gives you: a private folder, JSON settings, the shared log, the
        // notification surface, the language the window is in, and background work the shell
        // cancels when this is switched off.
        //
        // The log stays in English on purpose. It is the thing that gets pasted into a bug
        // report, and a report nobody can read is not much of a report.
        _host.Log("MyPlugin said hello.");
        Status = _host.Text.Format("PLUGIN-ID.said", DateTime.Now.ToString("HH:mm:ss"));
    }

    /// <summary>
    /// The line on this plugin's Home card: what the tab would say if asked "anything?", or null
    /// until there is news. From memory, never from the disk. Set IsTrouble only for something
    /// that wants doing.
    /// </summary>
    public Glance? Glance() => null;

    /// <summary>Ctrl+K reaching into this tab. Answer from what is already loaded, never from the disk.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit) => [];

    public void Dispose() => _language.Dispose();
}
