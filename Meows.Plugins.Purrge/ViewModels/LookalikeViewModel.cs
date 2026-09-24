using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Meows.Disk;
using Meows.Media;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Purrge.Services;

namespace Meows.Plugins.Purrge.ViewModels;

/// <summary>One picture in a group of look-alikes, as the list shows it.</summary>
public sealed class LookalikeFileViewModel(LookalikeFile file) : ObservableObject
{
    private bool _isKeeper;
    private bool _isSelected;

    public LookalikeFile File { get; } = file;

    public string Path => File.Path;

    public string FileName => System.IO.Path.GetFileName(File.Path);

    public string Folder => System.IO.Path.GetDirectoryName(File.Path) ?? "";

    public string SizeText => DuplicateSetViewModel.Format(File.Size);

    public string PixelsText => File.Duration is { } length
        ? $"{File.Width} × {File.Height} · {(length.TotalHours >= 1 ? length.ToString(@"h\:mm\:ss") : length.ToString(@"m\:ss"))}"
        : $"{File.Width} × {File.Height}";

    public bool IsVideo => File.IsVideo;

    public string DistanceText => File.Distance == 0 ? "" : MeowsText.Current.Format("purrge.lookalike.distance", File.Distance);

    /// <summary>The copy suggested to keep: the most pixels, then the most bytes. A person can pick another.</summary>
    public bool IsKeeper
    {
        get => _isKeeper;
        set => SetField(ref _isKeeper, value);
    }

    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

    internal void Reread() => OnEverythingChanged();
}

/// <summary>A group of look-alikes: the files, the one suggested to keep, and what the rest take up.</summary>
public sealed class LookalikeSetViewModel : ObservableObject
{
    public LookalikeSetViewModel(LookalikeSet set)
    {
        Set = set;
        foreach (var file in set.Files)
            Files.Add(new LookalikeFileViewModel(file));
        Files[0].IsKeeper = true;
    }

    public LookalikeSet Set { get; }

    public ObservableCollection<LookalikeFileViewModel> Files { get; } = [];

    public LookalikeFileViewModel Keeper => Files.First(f => f.IsKeeper);

    public string Header => MeowsText.Current.Format("purrge.lookalike.set", Files.Count,
        DuplicateSetViewModel.Format(Files.Where(f => !f.IsKeeper).Sum(f => f.File.Size)));

    internal void Reread()
    {
        OnPropertyChanged(nameof(Header));
        foreach (var file in Files)
            file.Reread();
    }
}

/// <summary>
/// Purrge's third mode: pictures that look alike without being the same bytes.
///
/// Built around the one rule that makes it safe: a look-alike is an opinion, so nothing goes to
/// the Recycle Bin until the picture and the one being kept are both on screen at full size,
/// side by side. There is no keep-one-bin-the-rest here, as there is for exact copies, because
/// "keep the newest" is safe for identical bytes and destructive for a guess.
/// </summary>
public sealed class LookalikeViewModel : ObservableObject, IDisposable
{
    private const int PreviewWidth = 720;

    public static readonly IReadOnlyList<(int Bits, string Key)> Strictness =
    [
        (2, "purrge.lookalike.strict"),
        (LookalikeScanner.DefaultThreshold, "purrge.lookalike.tight"),
        (8, "purrge.lookalike.loose"),
    ];

    private readonly IMeowsHost _host;
    private readonly Func<PurrgeSettings> _settings;
    private readonly Action _save;
    private readonly LookalikeScanner _scanner = new();

    private IBackgroundTask? _task;
    private LookalikeSetViewModel? _selectedSet;
    private LookalikeFileViewModel? _selectedFile;
    private Bitmap? _keeperPreview;
    private Bitmap? _selectedPreview;
    private bool _isRunning;
    private string? _status;
    private int _previewGeneration;

    public LookalikeViewModel(IMeowsHost host, Func<PurrgeSettings> settings, Action save)
    {
        _host = host;
        _settings = settings;
        _save = save;
        CancelCommand = new RelayCommand(() => _task?.Cancel(), () => IsRunning);
        SelectFileCommand = new RelayCommand(p => SelectFile(p as LookalikeFileViewModel));
        KeepThisCommand = new RelayCommand(KeepSelected, () => SelectedFile is { IsKeeper: false });
        RecycleCommand = new RelayCommand(() => _ = RecycleSelectedAsync(), () => CanRecycle);
        RevealCommand = new RelayCommand(() => { if (SelectedFile is { } f) Reveal(f.Path); }, () => SelectedFile is not null);
    }

    public ObservableCollection<LookalikeSetViewModel> Sets { get; } = [];

    public RelayCommand CancelCommand { get; }

    public RelayCommand SelectFileCommand { get; }

    /// <summary>The better copy wins: this one becomes the one kept, and the old keeper can be looked at against it.</summary>
    public RelayCommand KeepThisCommand { get; }

    public RelayCommand RecycleCommand { get; }

    public RelayCommand RevealCommand { get; }

    public IReadOnlyList<string> StrictnessChoices => Strictness.Select(s => MeowsText.Current[s.Key]).ToList();

    /// <summary>How close counts, as an index into <see cref="Strictness"/>. Tight by default.</summary>
    public int StrictnessIndex
    {
        get
        {
            var bits = _settings().LookalikeThreshold;
            var index = Strictness.ToList().FindIndex(s => s.Bits == bits);
            return index < 0 ? 1 : index;
        }
        set
        {
            if (value < 0 || value >= Strictness.Count || _settings().LookalikeThreshold == Strictness[value].Bits)
                return;
            _settings().LookalikeThreshold = Strictness[value].Bits;
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
            CancelCommand.RaiseCanExecuteChanged();
            RecycleCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status ?? MeowsText.Current["purrge.lookalike.start"];
        private set => SetField(ref _status, value);
    }

    public bool HasSets => Sets.Count > 0;

    public LookalikeSetViewModel? SelectedSet
    {
        get => _selectedSet;
        private set
        {
            if (!SetField(ref _selectedSet, value))
                return;
            OnPropertyChanged(nameof(HasSelection));
            OnPropertyChanged(nameof(Keeper));
        }
    }

    /// <summary>The picture being looked at against the keeper. Never the keeper itself.</summary>
    public LookalikeFileViewModel? SelectedFile
    {
        get => _selectedFile;
        private set
        {
            var previous = _selectedFile;
            if (!SetField(ref _selectedFile, value))
                return;
            if (previous is not null)
                previous.IsSelected = false;
            if (value is not null)
                value.IsSelected = true;
            OnPropertyChanged(nameof(HasSelection));
            KeepThisCommand.RaiseCanExecuteChanged();
            RevealCommand.RaiseCanExecuteChanged();
            RecycleCommand.RaiseCanExecuteChanged();
        }
    }

    public LookalikeFileViewModel? Keeper => _selectedSet?.Keeper;

    public bool HasSelection => _selectedFile is not null;

    public Bitmap? KeeperPreview
    {
        get => _keeperPreview;
        private set
        {
            var old = _keeperPreview;
            if (!SetField(ref _keeperPreview, value))
                return;
            old?.Dispose();
            OnPropertyChanged(nameof(CanRecycle));
            RecycleCommand.RaiseCanExecuteChanged();
        }
    }

    public Bitmap? SelectedPreview
    {
        get => _selectedPreview;
        private set
        {
            var old = _selectedPreview;
            if (!SetField(ref _selectedPreview, value))
                return;
            old?.Dispose();
            OnPropertyChanged(nameof(CanRecycle));
            RecycleCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>
    /// Only with both pictures on screen, and never the keeper. The whole safety of this mode is
    /// that nobody bins a guess without having looked at it next to what stays.
    /// </summary>
    public bool CanRecycle =>
        !IsRunning && _selectedFile is { IsKeeper: false } && _keeperPreview is not null && _selectedPreview is not null;

    public void Start(string root)
    {
        if (IsRunning || string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;

        ClearResults();
        IsRunning = true;
        Status = _host.Text["purrge.lookalike.looking"];
        var settings = _settings();
        var options = new Services.ScanOptions(settings.MinimumBytes, settings.SkipSystemFolders);
        var threshold = settings.LookalikeThreshold;
        var videos = settings.LookalikeVideos;
        var tools = videos ? VideoLooks.Tools() : null;
        _videosSkipped = videos && tools is null;

        _task = _host.Background.Run(_host.Text.Format("purrge.lookalike.task", Path.GetFileName(root.TrimEnd(Path.DirectorySeparatorChar))), async context =>
        {
            var progress = new Progress<Services.ScanProgress>(p => context.Report(p.Detail));
            try
            {
                var found = await _scanner.ScanAsync(root, options, threshold, progress, context.Token);
                if (tools is { } both)
                    found = [.. found, .. await _scanner.ScanVideosAsync(root, options, threshold, both, progress, context.Token)];
                await Dispatcher.UIThread.InvokeAsync(() => Show(found));
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
    }

    /// <summary>The groups found, the first one open.</summary>
    public void Show(IReadOnlyList<LookalikeSet> found)
    {
        ClearResults();
        foreach (var set in found)
            Sets.Add(new LookalikeSetViewModel(set));
        OnPropertyChanged(nameof(HasSets));

        Status = found.Count == 0
            ? _host.Text["purrge.lookalike.none"]
            : _host.Text.Format("purrge.lookalike.found", found.Count, DuplicateSetViewModel.Format(found.Sum(s => s.OthersBytes)));
        if (_videosSkipped)
            Status += " " + _host.Text["purrge.lookalike.noffmpeg"];
        _host.Log($"Look-alikes: {found.Count} group(s).");

        if (Sets.FirstOrDefault() is { } first)
            SelectFile(first.Files.FirstOrDefault(f => !f.IsKeeper));
    }

    private void ClearResults()
    {
        Sets.Clear();
        SelectedFile = null;
        SelectedSet = null;
        KeeperPreview = null;
        SelectedPreview = null;
        OnPropertyChanged(nameof(HasSets));
    }

    private void SelectFile(LookalikeFileViewModel? file)
    {
        if (file is null)
            return;
        var set = Sets.FirstOrDefault(s => s.Files.Contains(file));
        if (set is null)
            return;
        if (file.IsKeeper)
        {
            // The keeper is always on the left; clicking it looks at the next one against it.
            file = set.Files.FirstOrDefault(f => !f.IsKeeper);
            if (file is null)
                return;
        }

        SelectedSet = set;
        SelectedFile = file;
        _ = LoadPreviewsAsync(set.Keeper.File, file.File);
    }

    private async Task LoadPreviewsAsync(LookalikeFile keeper, LookalikeFile other)
    {
        var generation = ++_previewGeneration;
        KeeperPreview = null;
        SelectedPreview = null;
        var (left, right) = await Task.Run(() => (Preview(keeper), Preview(other)));
        if (generation != _previewGeneration)
        {
            left?.Dispose();
            right?.Dispose();
            return;
        }
        KeeperPreview = left;
        SelectedPreview = right;
    }

    /// <summary>A picture at full preview size; a video by its middle frame, so the two are still looked at side by side.</summary>
    private static Bitmap? Preview(LookalikeFile file)
    {
        if (!file.IsVideo)
            return AccessTime.Preserving(file.Path, () => Thumbnails.FromFile(file.Path, PreviewWidth));
        if (VideoLooks.Tools() is not { } tools)
            return null;
        var frame = AccessTime.Preserving(file.Path, () => VideoLooks.Frame(file.Path, file.Duration!.Value / 2, tools.Ffmpeg, PreviewWidth));
        return Thumbnails.FromBytes(frame, PreviewWidth);
    }

    /// <summary>Whether the last search wanted videos and could not have them, which the status line says.</summary>
    private bool _videosSkipped;

    /// <summary>Videos too: slower, and only with ffmpeg installed.</summary>
    public bool IncludeVideos
    {
        get => _settings().LookalikeVideos;
        set
        {
            if (_settings().LookalikeVideos == value)
                return;
            _settings().LookalikeVideos = value;
            _save();
            OnPropertyChanged();
        }
    }

    private void KeepSelected()
    {
        if (SelectedSet is not { } set || SelectedFile is not { IsKeeper: false } chosen)
            return;
        var old = set.Keeper;
        old.IsKeeper = false;
        chosen.IsKeeper = true;
        set.Reread();
        OnPropertyChanged(nameof(Keeper));
        SelectFile(old);
    }

    /// <summary>
    /// To the Recycle Bin, one picture at a time, with both on screen. Recorded, so History says
    /// what it looked like, and so the file can be found again in the bin.
    /// </summary>
    private async Task RecycleSelectedAsync()
    {
        if (!CanRecycle || SelectedSet is not { } set || SelectedFile is not { } file)
            return;

        var keeper = set.Keeper;
        var outcome = await Task.Run(() => RecycleBin.Send([file.Path]));
        if (outcome.Failed > 0)
        {
            Status = _host.Text.Format("purrge.lookalike.failed", file.FileName, outcome.FailureReason ?? "");
            return;
        }

        _host.Store.Record("recycled", file.Path, _host.Text.Format("purrge.lookalike.journal", keeper.FileName),
            new Dictionary<string, string> { ["size"] = file.File.Size.ToString(), ["kept"] = keeper.Path, ["distance"] = file.File.Distance.ToString() });
        _host.Log($"Recycled look-alike {file.Path}; kept {keeper.Path}.");

        set.Files.Remove(file);
        if (set.Files.Count < 2)
        {
            var index = Sets.IndexOf(set);
            Sets.Remove(set);
            OnPropertyChanged(nameof(HasSets));
            SelectedFile = null;
            SelectedSet = null;
            KeeperPreview = null;
            SelectedPreview = null;
            if (Sets.ElementAtOrDefault(Math.Min(index, Sets.Count - 1)) is { } next)
                SelectFile(next.Files.FirstOrDefault(f => !f.IsKeeper));
        }
        else
        {
            set.Reread();
            SelectFile(set.Files.FirstOrDefault(f => !f.IsKeeper));
        }
        Status = _host.Text.Format("purrge.lookalike.recycled", file.FileName, keeper.FileName);
    }

    private void Reveal(string path)
    {
        try
        {
            Explorer.Reveal(path);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not show {path}: {ex.Message}");
        }
    }

    internal void Reread()
    {
        OnEverythingChanged();
        foreach (var set in Sets)
            set.Reread();
    }

    public void Dispose()
    {
        _task?.Cancel();
        KeeperPreview = null;
        SelectedPreview = null;
    }
}
