using System.Collections.ObjectModel;
using Meows.Plugins.Abstractions;
using Meows.Services;

namespace Meows.ViewModels;

/// <summary>One source's setting on the Log tab: everything, warnings and errors, or nothing.</summary>
public sealed class LogSourceViewModel(string source, LogLevel minimum, Action<string, LogLevel> changed) : ObservableObject
{
    private LogLevel _minimum = minimum;

    public string Source { get; } = source;

    /// <summary>The name to show, which follows the feline switch for a plugin's source.</summary>
    public string Name => PluginNames.Display(Source);

    public IReadOnlyList<LogChoice> Choices => LogViewModel.Choices;

    public LogChoice Selected
    {
        get => Choices.First(c => c.Minimum == _minimum);
        set
        {
            if (value is null || value.Minimum == _minimum)
                return;
            _minimum = value.Minimum;
            OnPropertyChanged();
            changed(Source, _minimum);
        }
    }

    public void Retranslate() => OnEverythingChanged();
}

/// <summary>A choice in the per-source dropdown, named through the string table.</summary>
public sealed record LogChoice(LogLevel Minimum, string Key)
{
    public override string ToString() => MeowsText.Current[Key];
}

/// <summary>
/// The log, but useful: this run's lines with a word to filter on, a source to narrow to, the
/// warnings and errors surfaced, and per source a say in how much of it is shown at all. The
/// file under %APPDATA% keeps everything regardless; the levels here decide what the pane and
/// the tab show, which is what "quiet" means for a plugin that has a lot to say.
/// </summary>
public sealed class LogViewModel : ObservableObject, IDisposable
{
    /// <summary>Beyond a plugin's own info, warning and error: nothing at all, which is the quiet mode.</summary>
    public const LogLevel Quiet = (LogLevel)99;

    public static readonly IReadOnlyList<LogChoice> Choices =
    [
        new(LogLevel.Info, "log.level.everything"),
        new(LogLevel.Warning, "log.level.warnings"),
        new(Quiet, "log.level.quiet"),
    ];

    private readonly ShellLog _log;
    private readonly Dictionary<string, LogLevel> _minimums;
    private readonly Action<IReadOnlyDictionary<string, LogLevel>> _save;
    private string _filter = "";
    private string? _source;
    private bool _onlyTrouble;

    public LogViewModel(ShellLog log, IReadOnlyDictionary<string, LogLevel> minimums, Action<IReadOnlyDictionary<string, LogLevel>> save)
    {
        _log = log;
        _minimums = new Dictionary<string, LogLevel>(minimums, StringComparer.OrdinalIgnoreCase);
        _save = save;

        ClearFilterCommand = new RelayCommand(() => { Filter = ""; SelectedSource = null; OnlyTrouble = false; });
        OpenFileCommand = new RelayCommand(OpenFile, () => _log.FilePath is not null);

        _log.Written += OnWritten;
        Rebuild();
    }

    /// <summary>What is shown, oldest first, after the filters and the per-source levels.</summary>
    public ObservableCollection<LogEntry> Visible { get; } = [];

    /// <summary>Every source seen this run, with its level, for the panel on the right.</summary>
    public ObservableCollection<LogSourceViewModel> Sources { get; } = [];

    public ObservableCollection<string> SourceChoices { get; } = [];

    public RelayCommand ClearFilterCommand { get; }

    public RelayCommand OpenFileCommand { get; }

    public string Filter
    {
        get => _filter;
        set
        {
            if (SetField(ref _filter, value ?? ""))
                Rebuild();
        }
    }

    /// <summary>A source to narrow to, as shown; null for all of them.</summary>
    public string? SelectedSourceChoice
    {
        get => _source is null ? MeowsText.Current["log.all"] : PluginNames.Display(_source);
        set
        {
            var chosen = value is null || value == MeowsText.Current["log.all"]
                ? null
                : Sources.FirstOrDefault(s => s.Name == value)?.Source;
            SelectedSource = chosen;
        }
    }

    public string? SelectedSource
    {
        get => _source;
        set
        {
            if (_source == value)
                return;
            _source = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(SelectedSourceChoice));
            Rebuild();
        }
    }

    /// <summary>Warnings and errors only, across every source, which is the view for "what went wrong".</summary>
    public bool OnlyTrouble
    {
        get => _onlyTrouble;
        set
        {
            if (SetField(ref _onlyTrouble, value))
                Rebuild();
        }
    }

    public int Warnings => _log.Entries.Count(e => e.IsWarning);

    public int Errors => _log.Entries.Count(e => e.IsError);

    public bool HasTrouble => Warnings + Errors > 0;

    /// <summary>The headline: how much since the start, and how much of it was trouble.</summary>
    public string SummaryText
    {
        get
        {
            var text = MeowsText.Current;
            var line = text.Format("log.summary", _log.Entries.Count, _log.StartedAt.ToString("HH:mm"));
            if (!HasTrouble)
                return line;
            return $"{line} {text.Format("log.summary.trouble", Warnings, Errors)}";
        }
    }

    public string ShownText => MeowsText.Current.Format("log.shown", Visible.Count);

    public bool IsEmpty => Visible.Count == 0;

    public LogLevel MinimumFor(string source) => _minimums.GetValueOrDefault(source, LogLevel.Info);

    private bool Passes(LogEntry entry)
    {
        var minimum = MinimumFor(entry.Source);
        if (minimum == Quiet || entry.Level < minimum)
            return false;
        if (_onlyTrouble && entry.Level == LogLevel.Info)
            return false;
        if (_source is not null && !string.Equals(entry.Source, _source, StringComparison.OrdinalIgnoreCase))
            return false;
        if (_filter.Trim() is { Length: > 0 } word && !entry.Message.Contains(word, StringComparison.CurrentCultureIgnoreCase)
            && !entry.Source.Contains(word, StringComparison.CurrentCultureIgnoreCase))
            return false;
        return true;
    }

    private void OnWritten(LogEntry entry)
    {
        if (Sources.All(s => !string.Equals(s.Source, entry.Source, StringComparison.OrdinalIgnoreCase)))
            AddSource(entry.Source);

        if (Passes(entry))
        {
            Visible.Add(entry);
            OnPropertyChanged(nameof(ShownText));
            OnPropertyChanged(nameof(IsEmpty));
        }

        if (entry.Level != LogLevel.Info)
            RaiseSummary();
        else
            OnPropertyChanged(nameof(SummaryText));
    }

    private void AddSource(string source)
    {
        Sources.Add(new LogSourceViewModel(source, MinimumFor(source), SetMinimum));
        SourceChoices.Add(PluginNames.Display(source));
    }

    private void SetMinimum(string source, LogLevel minimum)
    {
        if (minimum == LogLevel.Info)
            _minimums.Remove(source);
        else
            _minimums[source] = minimum;
        _save(_minimums);
        Rebuild();
    }

    private void Rebuild()
    {
        Visible.Clear();
        foreach (var entry in _log.Entries)
        {
            if (Sources.All(s => !string.Equals(s.Source, entry.Source, StringComparison.OrdinalIgnoreCase)))
                AddSource(entry.Source);
            if (Passes(entry))
                Visible.Add(entry);
        }

        if (SourceChoices.Count == 0 || SourceChoices[0] != MeowsText.Current["log.all"])
            SourceChoices.Insert(0, MeowsText.Current["log.all"]);

        OnPropertyChanged(nameof(ShownText));
        OnPropertyChanged(nameof(IsEmpty));
        RaiseSummary();
    }

    private void RaiseSummary()
    {
        OnPropertyChanged(nameof(Warnings));
        OnPropertyChanged(nameof(Errors));
        OnPropertyChanged(nameof(HasTrouble));
        OnPropertyChanged(nameof(SummaryText));
    }

    private void OpenFile()
    {
        if (_log.FilePath is null)
            return;
        try
        {
            Explorer.Reveal(_log.FilePath);
        }
        catch (Exception)
        {
        }
    }

    /// <summary>Everything worked out in code reads differently now, the source names included.</summary>
    public void Retranslate()
    {
        foreach (var source in Sources)
            source.Retranslate();
        SourceChoices.Clear();
        SourceChoices.Add(MeowsText.Current["log.all"]);
        foreach (var source in Sources)
            SourceChoices.Add(source.Name);
        OnPropertyChanged(nameof(SelectedSourceChoice));
        OnEverythingChanged();
    }

    public void Dispose() => _log.Written -= OnWritten;
}
