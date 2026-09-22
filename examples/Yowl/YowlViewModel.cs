using Avalonia.Threading;
using Meows.Plugins.Abstractions;

namespace Yowl;

/// <summary>What Yowl remembers between runs. Plain JSON in the plugin's own settings file.</summary>
public sealed class YowlSettings
{
    public decimal MinimumFreeGb { get; set; } = 10;

    /// <summary>Somebody pressed "Not today": no yowling until then.</summary>
    public DateTime? QuietUntil { get; set; }
}

/// <summary>
/// Yowls when the Windows drive is running out of room. A schedule looks every ten minutes,
/// a condition goes up while there is too little and comes down when there is enough, and
/// the condition carries two buttons: look again now, and be quiet until tomorrow.
///
/// The shape to copy: the schedule is registered once in the constructor and the shell owns
/// it from then on; the check runs on a thread pool thread and marshals back before touching
/// anything bound; the condition has one key, so a repeated check replaces rather than piles up.
/// </summary>
public sealed class YowlViewModel : ObservableObject, IDisposable, IGlanceable
{
    private const string ConditionKey = "space";

    private readonly IMeowsHost _host;
    private readonly YowlSettings _settings;
    private readonly IBackgroundTask _watch;
    private readonly LanguageWatch _language;
    private string? _status;
    private decimal? _freeGb;

    public YowlViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<YowlSettings>() ?? new();
        _language = new LanguageWatch(OnEverythingChanged);
        LookNowCommand = new RelayCommand(LookNow);

        // Ten minutes between passes, not on a fixed clock, so a slow pass never overlaps the
        // next. The first pass runs at once. Purr lists this on its tab under Yowl.
        _watch = host.Background.Schedule(host.Text["yowl.watch"], TimeSpan.FromMinutes(10), Check);
    }

    public RelayCommand LookNowCommand { get; }

    /// <summary>The drive Windows is on, which is the one that hurts when it fills.</summary>
    public static string Drive => Path.GetPathRoot(Environment.SystemDirectory) ?? @"C:\";

    public decimal? MinimumFreeGb
    {
        get => _settings.MinimumFreeGb;
        set
        {
            if (value is not { } gb || gb == _settings.MinimumFreeGb)
                return;
            _settings.MinimumFreeGb = gb;
            _host.SaveSettings(_settings);
            OnPropertyChanged();
            LookNow();
        }
    }

    /// <summary>
    /// Null until the first pass, so the opening line follows the language; after that it says
    /// what the last pass found, in the language it was found in.
    /// </summary>
    public string Status
    {
        get => _status ?? _host.Text["yowl.status.start"];
        private set => SetField(ref _status, value);
    }

    public bool IsLow => _freeGb is { } free && free < _settings.MinimumFreeGb;

    public bool IsQuiet => _settings.QuietUntil is { } until && until > DateTime.Now;

    /// <summary>On the Home tab: the last reading, red while it is under the line.</summary>
    public Glance? Glance() => _freeGb is null ? null : new Glance(Status, IsLow);

    private void LookNow() => _host.Background.Run(_host.Text["yowl.look"], Check);

    private async Task Check(IBackgroundContext context)
    {
        context.Report(_host.Text.Format("yowl.checking", Drive));
        var free = await Task.Run(() => new DriveInfo(Drive).AvailableFreeSpace, context.Token);
        var freeGb = Math.Round(free / 1024m / 1024m / 1024m, 1);

        // Everything bound is touched on the UI thread and nowhere else.
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            _freeGb = freeGb;
            Status = _host.Text.Format("yowl.status", Drive, freeGb, DateTime.Now.ToString("HH:mm"));
            OnPropertyChanged(nameof(IsLow));
            OnPropertyChanged(nameof(IsQuiet));
            Yowl();
        });
    }

    /// <summary>
    /// A condition, not an event: it is a state of the world, it replaces itself under the same
    /// key, and only this plugin knows when it no longer applies, so only this plugin takes it
    /// down. The shell retracts it when the plugin is switched off.
    /// </summary>
    private void Yowl()
    {
        if (!IsLow || IsQuiet)
        {
            _host.Notifications.ClearCondition(ConditionKey);
            return;
        }

        var text = _host.Text;
        _host.Log(LogLevel.Warning, $"Yowl: {Drive} has {_freeGb} GB free, under the {_settings.MinimumFreeGb} GB line.");
        _host.Notifications.SetCondition(ConditionKey, NotificationSeverity.Warning,
            text.Format("yowl.notify.title", Drive),
            text.Format("yowl.notify.message", _freeGb, _settings.MinimumFreeGb),
            new NotificationAction(text["yowl.notify.again"], LookNow),
            new NotificationAction(text["yowl.notify.quiet"], QuietUntilTomorrow));
    }

    private void QuietUntilTomorrow()
    {
        _settings.QuietUntil = DateTime.Today.AddDays(1);
        _host.SaveSettings(_settings);
        OnPropertyChanged(nameof(IsQuiet));
        _host.Notifications.ClearCondition(ConditionKey);
        _host.Log("Yowl: quiet until tomorrow.");
    }

    public void Dispose()
    {
        // The shell cancels the schedule and retracts the condition on its own; disposing the
        // handle here is what makes that true even for a view model disposed some other way.
        _watch.Dispose();
        _language.Dispose();
    }
}
