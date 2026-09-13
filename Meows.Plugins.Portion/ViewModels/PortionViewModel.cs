using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Meows.Bot;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Portion.Services;

namespace Meows.Plugins.Portion.ViewModels;

public sealed class PortionSettings
{
    public string? BotRoot { get; set; }
}

/// <summary>One file that will not go, as a row.</summary>
public sealed class HeavyViewModel : ObservableObject, IDisposable
{
    private Bitmap? _thumbnail;
    private bool _isSelected;
    private bool _isBusy;
    private string? _outcome;
    private bool _failed;

    public HeavyViewModel(Heavy heavy) => Heavy = heavy;

    public Heavy Heavy { get; }

    public string GroupName => Heavy.Group.Name;

    public string FileName => Path.GetFileName(Heavy.Path);

    public string SizeText => Humanise(Heavy.Size);

    public string LimitText => Humanise(Heavy.Limit);

    public bool IsComic => Heavy.Kind == MediaKind.Comic;

    public bool IsVideo => Heavy.Kind is MediaKind.Video or MediaKind.Animation;

    /// <summary>The bot would fail on it, rather than merely surprise.</summary>
    public bool WillFail => Heavy.WillFail;

    public bool IsOnlyANote => !Heavy.WillFail;

    public bool CanShrink => Heavy.CanShrink && !_isBusy && _outcome is null;

    /// <summary>What is wrong, in words.</summary>
    public string TroubleText
    {
        get
        {
            var text = MeowsText.Current;
            var parts = new List<string>();
            foreach (var trouble in Heavy.Troubles)
            {
                parts.Add(trouble switch
                {
                    Trouble.OverBytes => text.Format("portion.trouble.bytes", SizeText, LimitText),
                    Trouble.TooManyPixels => text.Format("portion.trouble.pixels", Heavy.Width, Heavy.Height, MediaRules.PhotoMaxDimensionSum),
                    Trouble.OddRatio => text.Format("portion.trouble.ratio", MediaRules.PhotoMaxRatio),
                    Trouble.HeavyPages => text.Format("portion.trouble.pages", Heavy.HeavyPageCount, Heavy.Pages),
                    Trouble.EmptyComic => text["portion.trouble.empty"],
                    Trouble.BadPages => text.Format("portion.trouble.badpages", Heavy.BadPageCount),
                    Trouble.ForeignFiles => text.Format("portion.trouble.foreign", Heavy.ForeignCount),
                    _ => text.Format("portion.trouble.batches", Heavy.Pages, Heavy.Batches),
                });
            }

            return string.Join(" ", parts);
        }
    }

    /// <summary>What Portion would do, or why it will not.</summary>
    public string RemedyText => MeowsText.Current[Heavy.Kind switch
    {
        _ when Heavy.Troubles.Contains(Trouble.OddRatio) => "portion.remedy.ratio",
        MediaKind.Photo => "portion.remedy.photo",
        MediaKind.Comic when Heavy.Troubles.Contains(Trouble.EmptyComic) => "portion.remedy.empty",
        MediaKind.Comic when Heavy.Troubles.Contains(Trouble.BadPages) => "portion.remedy.badpages",
        MediaKind.Comic when Heavy.CanShrink => "portion.remedy.comic",
        MediaKind.Comic => "portion.remedy.comic.note",
        MediaKind.Video or MediaKind.Animation => "portion.remedy.video",
        _ => "portion.remedy.document",
    }];

    public bool IsBusy
    {
        get => _isBusy;
        set
        {
            if (SetField(ref _isBusy, value))
                OnPropertyChanged(nameof(CanShrink));
        }
    }

    public string? Outcome
    {
        get => _outcome;
        set
        {
            if (!SetField(ref _outcome, value))
                return;
            OnPropertyChanged(nameof(HasOutcome));
            OnPropertyChanged(nameof(CanShrink));
        }
    }

    public bool HasOutcome => _outcome is not null;

    public bool Failed
    {
        get => _failed;
        set => SetField(ref _failed, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        set
        {
            var old = _thumbnail;
            if (!SetField(ref _thumbnail, value))
                return;
            OnPropertyChanged(nameof(HasThumbnail));
            old?.Dispose();
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public static string Humanise(long bytes) => bytes switch
    {
        >= 1_000_000_000 => $"{bytes / 1_000_000_000.0:0.##} GB",
        >= 1_000_000 => $"{bytes / 1_000_000.0:0.#} MB",
        >= 1_000 => $"{bytes / 1_000.0:0} kB",
        _ => $"{bytes} B",
    };

    internal void Reread() => OnEverythingChanged();

    public void Dispose() => Thumbnail = null;
}

public sealed class PortionViewModel : ObservableObject, IDisposable
{
    private const int ThumbnailWidth = 56;
    private const int PreviewWidth = 720;

    private readonly IMeowsHost _host;
    private readonly PortionSettings _settings;
    private readonly LanguageWatch _language;
    private IBackgroundTask? _scan;
    private CancellationTokenSource? _thumbnails;
    private BotWorkspace? _workspace;

    private HeavyViewModel? _selected;
    private Bitmap? _preview;
    private bool _isScanning;
    private bool _isShrinking;
    private bool _scanned;
    private string? _status;
    private string? _errorMessage;

    public PortionViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<PortionSettings>() ?? new PortionSettings();

        ScanCommand = new RelayCommand(StartScan, () => !IsBusy);
        CancelCommand = new RelayCommand(() => _scan?.Cancel(), () => IsScanning);
        ShrinkSelectedCommand = new RelayCommand(() => _ = ShrinkAsync(Selected is { } s ? [s] : []), () => !IsBusy && Selected is { CanShrink: true });
        ShrinkAllCommand = new RelayCommand(() => _ = ShrinkAsync(Heavies.Where(h => h.CanShrink).ToList()), () => !IsBusy && Heavies.Any(h => h.CanShrink));
        SelectCommand = new RelayCommand(p => Selected = p as HeavyViewModel);
        RevealCommand = new RelayCommand(Reveal, () => Selected is not null);

        _language = new LanguageWatch(() =>
        {
            OnEverythingChanged();
            foreach (var heavy in Heavies)
                heavy.Reread();
        });

        StartScan();
    }

    public ObservableCollection<HeavyViewModel> Heavies { get; } = [];

    public RelayCommand ScanCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand ShrinkSelectedCommand { get; }

    public RelayCommand ShrinkAllCommand { get; }

    public RelayCommand SelectCommand { get; }

    public RelayCommand RevealCommand { get; }

    public string BotRootText => _workspace?.Root ?? MeowsText.Current["portion.nobot"];

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetField(ref _isScanning, value))
                RaiseCommands();
        }
    }

    public bool IsBusy => _isScanning || _isShrinking;

    public bool IsEmpty => Heavies.Count == 0;

    /// <summary>The good outcome, which deserves saying rather than a blank list.</summary>
    public bool AllClear => _scanned && !_isScanning && Heavies.Count == 0 && _workspace is { LooksValid: true };

    public string Status
    {
        get => _status ?? MeowsText.Current["portion.status.start"];
        private set => SetField(ref _status, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetField(ref _errorMessage, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public HeavyViewModel? Selected
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
            RaiseCommands();
            _ = ShowPreviewAsync(value);
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

    public void SetBotRoot(string root)
    {
        _settings.BotRoot = root;
        BotLocation.Remember(root);
        Save();
        StartScan();
    }

    /// <summary>Through the shell: it opens every comic in every queue, which is real work.</summary>
    private void StartScan()
    {
        if (IsBusy)
            return;

        ErrorMessage = null;
        Clear();

        var root = BotLocation.Resolve(_settings.BotRoot);
        _workspace = root is null ? null : new BotWorkspace(root);
        OnPropertyChanged(nameof(BotRootText));

        if (_workspace is not { LooksValid: true } workspace)
        {
            ErrorMessage = _host.Text["portion.error.nobot"];
            OnPropertyChanged(nameof(AllClear));
            return;
        }

        IsScanning = true;
        Status = _host.Text["portion.status.weighing"];

        _scan = _host.Background.Run(_host.Text["portion.task.weigh"], async context =>
        {
            try
            {
                var config = workspace.LoadConfig();
                var found = Weigher.Weigh(workspace, config,
                    group => context.Report(_host.Text.Format("portion.progress", group)),
                    context.Token);

                await Dispatcher.UIThread.InvokeAsync(() => Apply(found));
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.UIThread.InvokeAsync(() => Status = _host.Text["portion.status.cancelled"]);
                throw;
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => ErrorMessage = _host.Text.Format("portion.error.read", ex.Message));
                throw;
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _scanned = true;
                    IsScanning = false;
                    OnPropertyChanged(nameof(AllClear));
                });
            }
        });
    }

    private void Apply(IReadOnlyList<Heavy> found)
    {
        foreach (var heavy in found)
            Heavies.Add(new HeavyViewModel(heavy));

        var failing = found.Count(h => h.WillFail);
        var fixable = found.Count(h => h.CanShrink);
        Status = found.Count == 0
            ? _host.Text["portion.status.clear"]
            : _host.Text.Format("portion.status.found", failing, fixable, found.Count - failing);

        _host.Log($"Portion weighed the queues: {failing} would fail, {fixable} shrinkable, {found.Count - failing} notes.");
        if (failing > 0)
            _host.Notifications.Post(NotificationSeverity.Warning, _host.Text["portion.notify.found"], Status);

        OnPropertyChanged(nameof(IsEmpty));
        RaiseCommands();
        Selected = Heavies.FirstOrDefault();
        _ = LoadThumbnailsAsync();
    }

    /// <summary>
    /// One at a time, each verified before its original goes. A row that fails stays in the
    /// list with the reason; a row that succeeds says what it weighs now and leaves the list
    /// on the next scan, not this one, so the before and after can be read.
    /// </summary>
    public async Task ShrinkAsync(IReadOnlyList<HeavyViewModel> rows)
    {
        if (IsBusy || rows.Count == 0)
            return;

        _isShrinking = true;
        RaiseCommands();
        ErrorMessage = null;

        var done = 0;
        long saved = 0;
        var failed = 0;

        try
        {
            foreach (var row in rows)
            {
                row.IsBusy = true;
                Status = _host.Text.Format("portion.status.shrinking", row.FileName);

                var outcome = await Task.Run(() => Slimmer.Shrink(row.Heavy));

                row.IsBusy = false;
                if (outcome.Ok)
                {
                    done++;
                    saved += outcome.Before - outcome.After;
                    row.Outcome = _host.Text.Format("portion.outcome.done",
                        HeavyViewModel.Humanise(outcome.Before), HeavyViewModel.Humanise(outcome.After));
                    _host.Log($"Portion shrank {row.Heavy.Path}: {outcome.Before} -> {outcome.After} bytes");
                    _host.Store.Record("shrunk", outcome.Path ?? row.Heavy.Path, row.Outcome, new Dictionary<string, string>
                    {
                        ["before"] = outcome.Before.ToString(),
                        ["after"] = outcome.After.ToString(),
                        ["group"] = row.GroupName,
                    });
                }
                else
                {
                    failed++;
                    row.Outcome = outcome.Error ?? _host.Text["portion.outcome.failed"];
                    row.Failed = true;
                    _host.Log($"Portion could not shrink {row.Heavy.Path}: {outcome.Error}");
                }
            }

            Status = _host.Text.Format("portion.status.shrunk", done, HeavyViewModel.Humanise(saved), failed);
            _host.Notifications.Post(failed > 0 ? NotificationSeverity.Warning : NotificationSeverity.Info,
                _host.Text["portion.notify.shrunk"], Status);
        }
        finally
        {
            _isShrinking = false;
            RaiseCommands();
        }
    }

    private async Task LoadThumbnailsAsync()
    {
        _thumbnails?.Cancel();
        _thumbnails = new CancellationTokenSource();
        var token = _thumbnails.Token;

        foreach (var row in Heavies.ToList())
        {
            if (token.IsCancellationRequested)
                return;

            try
            {
                var bitmap = await Task.Run(() => Decode(row.Heavy.Path, ThumbnailWidth), token);
                await Dispatcher.UIThread.InvokeAsync(() => row.Thumbnail = bitmap);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
            }
        }
    }

    private async Task ShowPreviewAsync(HeavyViewModel? row)
    {
        Preview = null;
        if (row is null)
            return;

        try
        {
            var bitmap = await Task.Run(() => Decode(row.Heavy.Path, PreviewWidth));
            if (ReferenceEquals(_selected, row))
                Preview = bitmap;
            else
                bitmap?.Dispose();
        }
        catch (Exception)
        {
        }
    }

    private static Bitmap? Decode(string path, int width) => MediaRules.Thumbnail(path, width);

    private void Reveal()
    {
        if (Selected is null)
            return;

        try
        {
            Explorer.Reveal(Selected.Heavy.Path);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("portion.error.open", ex.Message);
        }
    }

    private void Clear()
    {
        _thumbnails?.Cancel();
        Selected = null;
        foreach (var heavy in Heavies)
            heavy.Dispose();
        Heavies.Clear();
        Preview = null;
        _scanned = false;
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(AllClear));
    }

    private void RaiseCommands()
    {
        OnPropertyChanged(nameof(IsBusy));
        ScanCommand.RaiseCanExecuteChanged();
        CancelCommand.RaiseCanExecuteChanged();
        ShrinkSelectedCommand.RaiseCanExecuteChanged();
        ShrinkAllCommand.RaiseCanExecuteChanged();
        RevealCommand.RaiseCanExecuteChanged();
    }

    private void Save()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log($"Could not save Portion settings: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _language.Dispose();
        _scan?.Dispose();
        _thumbnails?.Cancel();
        foreach (var heavy in Heavies)
            heavy.Dispose();
        Preview = null;
    }
}
