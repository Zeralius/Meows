using Meows.Plugins.Abstractions;

namespace MyPlugin;

public sealed class MyPluginViewModel : ObservableObject
{
    private readonly IMeowsHost _host;
    private string _status;

    public MyPluginViewModel(IMeowsHost host)
    {
        _host = host;
        _status = host.Text.Format("PLUGIN-ID.where", host.DataDirectory);

        SayHelloCommand = new RelayCommand(SayHello);
    }

    public RelayCommand SayHelloCommand { get; }

    public string Status
    {
        get => _status;
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
}
