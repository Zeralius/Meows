using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Purrge.Services;

namespace Meows.Plugins.Purrge.ViewModels;

/// <summary>One row in the compare result: what is wrong, where, and by how much.</summary>
public sealed class FindingViewModel : ObservableObject
{
    private bool _isSelected;

    public FindingViewModel(Finding finding) => Finding = finding;

    public Finding Finding { get; }

    public FindingKind Kind => Finding.Kind;

    public string RelativePath => Finding.RelativePath;

    public string FileName => Path.GetFileName(Finding.RelativePath);

    public string Folder => Path.GetDirectoryName(Finding.RelativePath) is { Length: > 0 } folder ? folder : ".";

    public bool IsMissing => Kind == FindingKind.Missing;

    public bool IsStale => Kind == FindingKind.Stale;

    public bool IsDifferent => Kind == FindingKind.Different;

    public bool IsExtra => Kind == FindingKind.Extra;

    public bool IsUnreadable => Kind == FindingKind.Unreadable;

    /// <summary>The side that exists, for the preview. The source unless there is only a copy.</summary>
    public string ExistingPath => Kind == FindingKind.Extra ? Finding.CopyPath : Finding.SourcePath;

    public bool HasSource => Finding.SourceSize is not null;

    public bool HasCopy => Finding.CopySize is not null;

    public string KindText => MeowsText.Current[Kind switch
    {
        FindingKind.Missing => "purrge.kind.missing",
        FindingKind.Stale => "purrge.kind.stale",
        FindingKind.Different => "purrge.kind.different",
        FindingKind.Extra => "purrge.kind.extra",
        _ => "purrge.kind.unreadable",
    }];

    public string SourceText => Describe(Finding.SourceSize, Finding.SourceModifiedUtc);

    public string CopyText => Describe(Finding.CopySize, Finding.CopyModifiedUtc);

    private static string Describe(long? size, DateTime? modifiedUtc) =>
        size is null
            ? MeowsText.Current["purrge.side.absent"]
            : $"{DuplicateSetViewModel.Format(size.Value)} · {modifiedUtc?.ToLocalTime():yyyy-MM-dd HH:mm}";

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    internal void Reread() => OnEverythingChanged();
}

/// <summary>
/// The second thing Purrge's tab does: check that a copy is really a copy.
///
/// Its own view model rather than more state on the main one, because it shares the tree and
/// the shell with the duplicate scan and nothing else. It has no delete, no keep and no copy, on
/// purpose, and the buttons it does have open Explorer and nothing more.
/// </summary>
public sealed class CompareViewModel : ObservableObject, IDisposable
{
    private const int PreviewWidth = 720;

    private readonly IMeowsHost _host;
    private readonly FolderComparer _comparer = new();
    private readonly Func<PurrgeSettings> _settings;
    private readonly Action _save;

    private IBackgroundTask? _task;
    private CompareReport? _report;
    private FindingViewModel? _selected;
    private Bitmap? _preview;
    private bool _isRunning;
    private string? _status;

    public CompareViewModel(IMeowsHost host, Func<PurrgeSettings> settings, Action save)
    {
        _host = host;
        _settings = settings;
        _save = save;

        CompareCommand = new RelayCommand(Start, () => !IsRunning && CanCompare);
        CancelCommand = new RelayCommand(() => _task?.Cancel(), () => IsRunning);
        SelectCommand = new RelayCommand(p => Selected = p as FindingViewModel);
        RevealSourceCommand = new RelayCommand(() => Reveal(Selected?.Finding.SourcePath), () => Selected is { HasSource: true });
        RevealCopyCommand = new RelayCommand(() => Reveal(Selected?.Finding.CopyPath), () => Selected is { HasCopy: true });
    }

    public ObservableCollection<FindingViewModel> Findings { get; } = [];

    public RelayCommand CompareCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand SelectCommand { get; }

    public RelayCommand RevealSourceCommand { get; }

    public RelayCommand RevealCopyCommand { get; }

    public string Source
    {
        get => _settings().CompareSource ?? "";
        set
        {
            if (Source == value)
                return;
            _settings().CompareSource = value;
            _save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasSource));
            CompareCommand.RaiseCanExecuteChanged();
        }
    }

    public string Copy
    {
        get => _settings().CompareCopy ?? "";
        set
        {
            if (Copy == value)
                return;
            _settings().CompareCopy = value;
            _save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasCopy));
            CompareCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSource => Source.Length > 0;

    public bool HasCopy => Copy.Length > 0;

    /// <summary>
    /// Both set, both real, and not one inside the other. A copy inside its own source would
    /// be compared against itself and would always pass.
    /// </summary>
    public bool CanCompare =>
        HasSource && HasCopy &&
        Directory.Exists(Source) && Directory.Exists(Copy) &&
        !SameOrNested(Source, Copy);

    public static bool SameOrNested(string a, string b)
    {
        var x = Path.GetFullPath(a).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        var y = Path.GetFullPath(b).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        return x.StartsWith(y, StringComparison.OrdinalIgnoreCase) || y.StartsWith(x, StringComparison.OrdinalIgnoreCase);
    }

    public bool TrustTimestamps
    {
        get => _settings().TrustTimestamps;
        set
        {
            if (_settings().TrustTimestamps == value)
                return;
            _settings().TrustTimestamps = value;
            _save();
            OnPropertyChanged();
        }
    }

    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (!SetField(ref _isRunning, value))
                return;
            CompareCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status ?? MeowsText.Current["purrge.compare.start"];
        private set => SetField(ref _status, value);
    }

    public bool HasReport => _report is not null;

    public bool HasFindings => Findings.Count > 0;

    /// <summary>The copy is complete and identical. The line everyone hopes to read.</summary>
    public bool CopyIsGood => _report is { CopyIsGood: true };

    public string Summary
    {
        get
        {
            if (_report is not { } report)
                return "";

            return _host.Text.Format("purrge.compare.summary",
                report.Checked, DuplicateSetViewModel.Format(report.BytesChecked),
                report.Count(FindingKind.Missing), report.Count(FindingKind.Stale),
                report.Count(FindingKind.Different), report.Count(FindingKind.Extra));
        }
    }

    /// <summary>How thorough it was, so a quick run is not mistaken for a full one.</summary>
    public string DepthText
    {
        get
        {
            if (_report is not { } report)
                return "";
            return _host.Text.Format("purrge.compare.depth", report.ReadInFull, report.Checked);
        }
    }

    public FindingViewModel? Selected
    {
        get => _selected;
        set
        {
            var previous = _selected;
            if (!SetField(ref _selected, value))
                return;
            if (previous is not null)
                previous.IsSelected = false;
            if (value is not null)
                value.IsSelected = true;
            OnPropertyChanged(nameof(HasSelection));
            RevealSourceCommand.RaiseCanExecuteChanged();
            RevealCopyCommand.RaiseCanExecuteChanged();
            _ = LoadPreviewAsync(value);
        }
    }

    public bool HasSelection => _selected is not null;

    public Bitmap? Preview
    {
        get => _preview;
        private set
        {
            var old = _preview;
            if (!SetField(ref _preview, value))
                return;
            OnPropertyChanged(nameof(HasPreview));
            old?.Dispose();
        }
    }

    public bool HasPreview => _preview is not null;

    /// <summary>Through the shell, so it shows in the task panel and dies with the plugin.</summary>
    private void Start()
    {
        Clear();
        IsRunning = true;

        var source = Source;
        var copy = Copy;
        var options = new CompareOptions(TrustTimestamps, _settings().SkipSystemFolders);

        _task = _host.Background.Run(
            _host.Text.Format("purrge.task.compare", Path.GetFileName(source.TrimEnd(Path.DirectorySeparatorChar))),
            async context =>
            {
                var total = 0;
                var progress = new Progress<ScanProgress>(p =>
                {
                    switch (p.Phase)
                    {
                        case ScanPhase.Enumerating:
                            context.Report(_host.Text.Format("purrge.progress.listing", p.FilesSeen.ToString("N0")));
                            context.ReportProgress(null);
                            break;
                        case ScanPhase.Hashing:
                            if (total == 0)
                                total = p.Candidates;
                            context.Report(_host.Text.Format("purrge.progress.comparing", p.Detail));
                            context.ReportProgress(total == 0 ? null : (double)(total - p.Candidates) / total);
                            break;
                        default:
                            context.Report(p.Detail);
                            break;
                    }
                });

                try
                {
                    var report = await _comparer.CompareAsync(source, copy, options, progress, context.Token);
                    await Dispatcher.UIThread.InvokeAsync(() => Apply(report));
                }
                catch (OperationCanceledException)
                {
                    await Dispatcher.UIThread.InvokeAsync(() => Status = _host.Text["purrge.status.cancelled"]);
                    throw;
                }
                finally
                {
                    await Dispatcher.UIThread.InvokeAsync(() => IsRunning = false);
                }
            });

        _host.Log($"Comparing {source} against {copy}");
    }

    private void Apply(CompareReport report)
    {
        _report = report;
        foreach (var finding in report.Findings)
            Findings.Add(new FindingViewModel(finding));

        Status = report.CopyIsGood
            ? _host.Text["purrge.compare.good"]
            : _host.Text.Format("purrge.compare.done", Summary);
        _host.Log($"Compare finished: {Summary}");

        _host.Notifications.Post(
            report.CopyIsGood ? NotificationSeverity.Info : NotificationSeverity.Warning,
            _host.Text["purrge.notify.compared"],
            report.CopyIsGood ? _host.Text["purrge.compare.good"] : Summary);

        RaiseReportState();
        Selected = Findings.FirstOrDefault();
    }

    private void Clear()
    {
        _report = null;
        Selected = null;
        Findings.Clear();
        Preview = null;
        RaiseReportState();
    }

    private void RaiseReportState()
    {
        OnPropertyChanged(nameof(HasReport));
        OnPropertyChanged(nameof(HasFindings));
        OnPropertyChanged(nameof(CopyIsGood));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(DepthText));
    }

    private async Task LoadPreviewAsync(FindingViewModel? finding)
    {
        if (finding is null)
        {
            Preview = null;
            return;
        }

        var path = finding.ExistingPath;
        var bitmap = PreviewSupport.IsRenderable(path)
            ? await Task.Run(() => PreviewSupport.Decode(path, PreviewWidth))
            : null;

        if (!ReferenceEquals(_selected, finding))
        {
            bitmap?.Dispose();
            return;
        }

        Preview = bitmap;
    }

    private void Reveal(string? path)
    {
        if (path is null)
            return;

        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "explorer.exe",
                ArgumentList = { "/select,", path },
                UseShellExecute = true,
            });
        }
        catch (Exception ex)
        {
            Status = _host.Text.Format("purrge.error.explorer", ex.Message);
        }
    }

    /// <summary>Everything worked out in code reads differently now.</summary>
    internal void Reread()
    {
        OnEverythingChanged();
        foreach (var finding in Findings)
            finding.Reread();
    }

    public void Dispose()
    {
        _task?.Dispose();
        Preview = null;
    }
}
