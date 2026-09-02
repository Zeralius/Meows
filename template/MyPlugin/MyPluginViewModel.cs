using Mews.Plugins.Abstractions;

namespace MyPlugin;

public sealed class MyPluginViewModel : ObservableObject
{
    private readonly IMewsHost _host;
    private string _status;

    public MyPluginViewModel(IMewsHost host)
    {
        _host = host;
        _status = $"Settings and anything else you save live in {host.DataDirectory}";

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
        // notification surface, and background work the shell cancels when this is switched off.
        _host.Log("MyPlugin said hello.");
        Status = $"Said hello at {DateTime.Now:HH:mm:ss}. Look in the Log at the bottom right.";
    }
}
