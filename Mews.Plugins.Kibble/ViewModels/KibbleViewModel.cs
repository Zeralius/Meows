using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mews.Bot;
using Mews.Plugins.Abstractions;
using Mews.Plugins.Kibble.Services;

namespace Mews.Plugins.Kibble.ViewModels;

public sealed class KibbleSettings
{
    public string? BotRoot { get; set; }

    public string? LastSourceFolder { get; set; }

    public IntakeStamp Stamp { get; set; } = IntakeStamp.KeepSource;

    public GridSort Sort { get; set; } = GridSort.NameAscending;

    public PageOrder PageOrder { get; set; } = PageOrder.ByName;
}

/// <summary>How the folder you opened is laid out in the middle column.</summary>
public enum GridSort
{
    NameAscending,
    NameDescending,
    NewestFirst,
    OldestFirst,
}

/// <summary>A sort with words on it, so the dropdown does not show an enum name.</summary>
public sealed record SortOption(GridSort Value, string Label)
{
    public override string ToString() => Label;
}

/// <summary>A page order with words on it.</summary>
public sealed record PageOrderOption(PageOrder Value, string Label)
{
    public override string ToString() => Label;
}

public sealed class KibbleViewModel : ObservableObject, IDisposable
{
    private const int ThumbnailWidth = 150;
    private const int PreviewWidth = 720;

    private readonly IMewsHost _host;
    private KibbleSettings _settings;
    private BotWorkspace? _workspace;
    private CancellationTokenSource? _thumbnailCts;

    private IncomingFileViewModel? _selected;
    private Bitmap? _previewImage;
    private string _archiveName = "";

    /// <summary>Everything highlighted in the grid. One entry is a plain send, more is a comic.</summary>
    private readonly List<IncomingFileViewModel> _selection = [];
    private string _sourceFolder = "";
    private string _statusMessage = "Open a folder to start.";
    private string? _errorMessage;
    private string? _blockedReason;

    /// <summary>Everything the last send moved, so it can be put back.</summary>
    private readonly List<IntakeResult> _lastBatch = [];

    public KibbleViewModel(IMewsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<KibbleSettings>() ?? new KibbleSettings();
        _sourceFolder = _settings.LastSourceFolder ?? "";

        SendToCommand = new RelayCommand(SendTo, CanSend);
        SkipCommand = new RelayCommand(SkipSelected, () => Selected is not null);
        RefreshCommand = new RelayCommand(() => LoadFolder(SourceFolder), () => SourceFolder.Length > 0);
        UndoCommand = new RelayCommand(UndoLastBatch, () => _lastBatch.Count > 0);
        OpenSourceCommand = new RelayCommand(() => OpenInExplorer(SourceFolder), () => SourceFolder.Length > 0);

        Reload();
        if (_sourceFolder.Length > 0 && Directory.Exists(_sourceFolder))
            LoadFolder(_sourceFolder);
    }

    public ObservableCollection<DestinationViewModel> Destinations { get; } = new();

    public ObservableCollection<IncomingFileViewModel> Incoming { get; } = new();

    public RelayCommand SendToCommand { get; }

    public RelayCommand SkipCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand UndoCommand { get; }

    public RelayCommand OpenSourceCommand { get; }

    public IReadOnlyList<StampOption> StampOptions { get; } =
    [
        new(IntakeStamp.KeepSource, "Keep each file's own date"),
        new(IntakeStamp.QueuedNow, "Date them as they are queued"),
    ];

    public StampOption SelectedStamp
    {
        get => StampOptions.First(o => o.Value == _settings.Stamp);
        set
        {
            if (value is null || _settings.Stamp == value.Value)
                return;
            _settings.Stamp = value.Value;
            SaveSettings();
            OnPropertyChanged();
            OnPropertyChanged(nameof(StampHint));
        }
    }

    private IntakeStamp Stamp => _settings.Stamp;

    /// <summary>
    /// Spelled out because it decides posting order, and getting it wrong is invisible until
    /// the queue behaves oddly weeks later.
    /// </summary>
    public string StampHint => Stamp == IntakeStamp.KeepSource
        ? "Files keep their original date, so genuinely older art posts first."
        : "Files are stamped as they are queued, so it is first in, first out.";

    public bool HasWorkspace => _workspace?.LooksValid == true;

    public string BotRootText => _workspace?.Root ?? "No bot folder found";

    public string SourceFolder
    {
        get => _sourceFolder;
        private set
        {
            if (!SetField(ref _sourceFolder, value))
                return;
            OnPropertyChanged(nameof(HasSource));
            RefreshCommand.RaiseCanExecuteChanged();
            OpenSourceCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSource => SourceFolder.Length > 0;

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
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

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    /// <summary>Why the selected file cannot go to the highlighted group, if it cannot.</summary>
    public string? BlockedReason
    {
        get => _blockedReason;
        private set
        {
            if (SetField(ref _blockedReason, value))
                OnPropertyChanged(nameof(IsBlocked));
        }
    }

    public bool IsBlocked => !string.IsNullOrEmpty(BlockedReason);

    public IncomingFileViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value))
                return;
            OnPropertyChanged(nameof(HasSelection));
            BlockedReason = null;
            SendToCommand.RaiseCanExecuteChanged();
            SkipCommand.RaiseCanExecuteChanged();
            _ = LoadPreviewAsync(value);
        }
    }

    public bool HasSelection => _selected is not null;

    /// <summary>
    /// Told to us by the grid, because ctrl and shift ranges are the list control's job and
    /// reimplementing them by hand would only get them subtly wrong.
    /// </summary>
    public void SetSelection(IEnumerable<IncomingFileViewModel> files)
    {
        // Keep the order things were picked in. Whatever the list control reports, anything
        // already picked holds its place and only genuinely new files go on the end, so the
        // page numbers do not shuffle when you add one more.
        var now = files.ToList();
        var live = now.ToHashSet();
        var ordered = _selection.Where(live.Contains).ToList();
        foreach (var file in now)
            if (!ordered.Contains(file))
                ordered.Add(file);

        _selection.Clear();
        _selection.AddRange(ordered);
        NumberThePicked();
        BlockedReason = null;
        OnPropertyChanged(nameof(SelectionCount));
        OnPropertyChanged(nameof(IsBundle));
        OnPropertyChanged(nameof(BundleText));
        OnPropertyChanged(nameof(SelectionText));
        OnPropertyChanged(nameof(SendVerb));
    }

    public IReadOnlyList<SortOption> SortOptions { get; } =
    [
        new(GridSort.NameAscending, "Name, A to Z"),
        new(GridSort.NameDescending, "Name, Z to A"),
        new(GridSort.NewestFirst, "Newest first"),
        new(GridSort.OldestFirst, "Oldest first"),
    ];

    public SortOption SelectedSort
    {
        get => SortOptions.First(o => o.Value == _settings.Sort);
        set
        {
            if (value is null || _settings.Sort == value.Value)
                return;
            _settings.Sort = value.Value;
            SaveSettings();
            OnPropertyChanged();
            ApplySort();
        }
    }

    public IReadOnlyList<PageOrderOption> PageOrderOptions { get; } =
    [
        new(PageOrder.ByName, "Pages in file name order"),
        new(PageOrder.AsPicked, "Pages in the order I picked them"),
    ];

    public PageOrderOption SelectedPageOrder
    {
        get => PageOrderOptions.First(o => o.Value == _settings.PageOrder);
        set
        {
            if (value is null || _settings.PageOrder == value.Value)
                return;
            _settings.PageOrder = value.Value;
            SaveSettings();
            OnPropertyChanged();
            NumberThePicked();
        }
    }

    public int SelectionCount => _selection.Count;

    /// <summary>
    /// Stamps 1, 2, 3 onto the picked tiles in the order they will appear in the comic, so the
    /// page order is something you can see rather than something you find out afterwards.
    /// </summary>
    private void NumberThePicked()
    {
        foreach (var file in Incoming)
            file.PageNumber = 0;

        if (_selection.Count < Intake.MinBundle)
            return;

        var page = 1;
        foreach (var file in PagesInOrder())
            file.PageNumber = page++;
    }

    /// <summary>The pick in the order it would be written into the archive.</summary>
    private IEnumerable<IncomingFileViewModel> PagesInOrder() =>
        _settings.PageOrder == PageOrder.ByName
            ? _selection.OrderBy(f => f.FileName, Comparer<string>.Create(MediaRules.CompareNatural))
            : _selection;

    /// <summary>Two or more picked means the next send makes a comic out of them.</summary>
    public bool IsBundle => _selection.Count >= Intake.MinBundle;

    public string SelectionText => _selection.Count > 1 ? $"{_selection.Count} picked" : "";

    /// <summary>What the destination buttons are about to do, so the left column stays honest.</summary>
    public string SendVerb => IsBundle ? "SEND AS ONE COMIC" : "SEND TO";

    public string BundleText => IsBundle
        ? $"{_selection.Count} files will be zipped into one comic and queued as a single post."
        : "";

    /// <summary>Name for the archive. Defaults to the folder you opened, since that is usually the set.</summary>
    public string ArchiveName
    {
        get => _archiveName;
        set => SetField(ref _archiveName, value);
    }

    public Bitmap? PreviewImage
    {
        get => _previewImage;
        private set
        {
            var old = _previewImage;
            if (!SetField(ref _previewImage, value))
                return;
            OnPropertyChanged(nameof(HasPreviewImage));
            old?.Dispose();
        }
    }

    public bool HasPreviewImage => _previewImage is not null;

    public string RemainingText => Incoming.Count switch
    {
        0 => "Nothing left",
        1 => "1 file left",
        _ => $"{Incoming.Count} files left",
    };

    public bool IsEmpty => Incoming.Count == 0;

    public string SummaryText
    {
        get
        {
            if (Destinations.Count == 0)
                return "";
            var dry = Destinations.Count(d => d.IsDry);
            var low = Destinations.Count(d => d.IsLow);
            if (dry == 0 && low == 0)
                return $"{Destinations.Count} group(s), all stocked";
            var parts = new List<string>();
            if (dry > 0) parts.Add($"{dry} dry");
            if (low > 0) parts.Add($"{low} running low");
            return string.Join(", ", parts);
        }
    }

    public void SetBotRoot(string path)
    {
        _settings.BotRoot = path;
        SaveSettings();
        Reload();
    }

    public void LoadFolder(string folder)
    {
        ErrorMessage = null;
        CancelThumbnails();
        ClearIncoming();

        if (!Directory.Exists(folder))
        {
            ErrorMessage = $"{folder} does not exist.";
            return;
        }

        SourceFolder = folder;
        _settings.LastSourceFolder = folder;
        SaveSettings();
        ArchiveName = Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));

        try
        {
            // Everything, not just what the bot can post. A file it cannot use is exactly
            // what you want to see and deal with, rather than have quietly hidden.
            foreach (var file in Sorted(Directory.EnumerateFiles(folder).Select(f => new IncomingFileViewModel(f))))
                Incoming.Add(file);
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not read {folder}: {ex.Message}";
            return;
        }

        Selected = Incoming.FirstOrDefault();
        StatusMessage = $"{Incoming.Count} file(s) in {Path.GetFileName(folder.TrimEnd(Path.DirectorySeparatorChar))}";
        RaiseGridState();
        _ = LoadThumbnailsAsync();
    }

    private IEnumerable<IncomingFileViewModel> Sorted(IEnumerable<IncomingFileViewModel> files) =>
        _settings.Sort switch
        {
            GridSort.NameDescending => files.OrderByDescending(f => f.FileName, Comparer<string>.Create(MediaRules.CompareNatural)),
            GridSort.NewestFirst => files.OrderByDescending(f => f.Modified),
            GridSort.OldestFirst => files.OrderBy(f => f.Modified),
            _ => files.OrderBy(f => f.FileName, Comparer<string>.Create(MediaRules.CompareNatural)),
        };

    /// <summary>
    /// Reorders what is already loaded rather than rereading the folder, so thumbnails already
    /// decoded stay decoded. The pick is dropped, because carrying a multi-file pick across a
    /// reorder would leave the numbers meaning something you can no longer see.
    /// </summary>
    private void ApplySort()
    {
        if (Incoming.Count == 0)
            return;

        var keep = Selected;
        var reordered = Sorted(Incoming.ToList()).ToList();

        Incoming.Clear();
        foreach (var file in reordered)
            Incoming.Add(file);

        SetSelection([]);
        Selected = keep is not null && Incoming.Contains(keep) ? keep : Incoming.FirstOrDefault();
    }

    /// <summary>Sends the selection to a destination, by click or by number key.</summary>
    private void SendTo(object? parameter)
    {
        if (_workspace is null || Selected is null)
            return;

        var destination = parameter as DestinationViewModel
                          ?? (parameter is int i ? Destinations.ElementAtOrDefault(i - 1) : null);
        if (destination is null)
            return;

        if (IsBundle)
        {
            SendBundle(destination);
            return;
        }

        var file = Selected;
        var result = Intake.Send(file.Path, _workspace, destination.Group, Stamp);

        if (!result.Moved)
        {
            // Leave it in the grid. It is still yours to deal with.
            BlockedReason = result.Detail;
            _host.Log($"Not sent: {file.FileName} to {destination.Name}: {result.Detail}");
            return;
        }

        _lastBatch.Clear();
        _lastBatch.Add(result);
        UndoCommand.RaiseCanExecuteChanged();

        var next = NextAfter(file);
        Incoming.Remove(file);
        file.Dispose();
        Selected = next;

        destination.Refresh();
        StatusMessage = $"Sent to {destination.Name}, {destination.RunwayText}";
        _host.Log($"Queued {Path.GetFileName(result.Destination!)} into {destination.Name}");
        RaiseGridState();
    }

    /// <summary>
    /// Zips the picked files into one comic in the group's queue. The bot posts a .cbz as a
    /// single comic, in batches of ten pages, so this turns a page set into one post rather
    /// than a run of unrelated ones.
    /// </summary>
    private void SendBundle(DestinationViewModel destination)
    {
        var files = PagesInOrder().ToList();

        // Already in page order here, so the archive is written exactly as the numbered tiles
        // promised rather than being sorted a second time behind your back.
        var result = Intake.SendAsComic(
            files.Select(f => f.Path).ToList(), _workspace!, destination.Group, Stamp, ArchiveName,
            PageOrder.AsPicked);

        if (!result.Moved)
        {
            // Same as a refused single file. Everything stays picked and in the grid.
            BlockedReason = result.Detail;
            _host.Log($"Not sent: comic to {destination.Name}: {result.Detail}");
            return;
        }

        _lastBatch.Clear();
        _lastBatch.Add(result);
        UndoCommand.RaiseCanExecuteChanged();

        var next = NextAfterAll(files);
        foreach (var file in files)
        {
            Incoming.Remove(file);
            file.Dispose();
        }

        SetSelection([]);
        Selected = next;

        destination.Refresh();
        StatusMessage = $"Sent {files.Count} pages as {Path.GetFileName(result.Destination!)}, {destination.RunwayText}";
        _host.Log($"Queued comic {Path.GetFileName(result.Destination!)} ({files.Count} pages) into {destination.Name}");
        RaiseGridState();
    }

    private bool CanSend(object? parameter) => _workspace is not null && Selected is not null;

    private void SkipSelected()
    {
        if (Selected is null)
            return;
        Selected = NextAfter(Selected);
    }

    /// <summary>Keeps you moving forwards through the grid rather than jumping to the top.</summary>
    private IncomingFileViewModel? NextAfter(IncomingFileViewModel current)
    {
        var index = Incoming.IndexOf(current);
        if (index < 0)
            return Incoming.FirstOrDefault();
        return Incoming.ElementAtOrDefault(index + 1) ?? Incoming.ElementAtOrDefault(index - 1);
    }

    /// <summary>Where to land after a bundle leaves, following the last page rather than the first.</summary>
    private IncomingFileViewModel? NextAfterAll(IReadOnlyList<IncomingFileViewModel> removed)
    {
        var taken = removed.ToHashSet();
        var last = removed.Select(f => Incoming.IndexOf(f)).Where(i => i >= 0).DefaultIfEmpty(-1).Max();

        for (var i = last + 1; i < Incoming.Count; i++)
            if (!taken.Contains(Incoming[i]))
                return Incoming[i];

        for (var i = Math.Min(last, Incoming.Count - 1); i >= 0; i--)
            if (!taken.Contains(Incoming[i]))
                return Incoming[i];

        return null;
    }

    private void UndoLastBatch()
    {
        var restored = 0;
        foreach (var result in _lastBatch)
        {
            if (!Intake.Undo(result))
                continue;

            // A bundle went in as many files and came back as many, so put all of them back
            // in the grid rather than just the one the result is named after.
            var paths = result.Bundled is { Count: > 0 } bundled
                ? bundled.Select(b => b.Path).ToList()
                : [result.SourcePath];

            foreach (var path in paths)
            {
                restored++;
                Incoming.Add(new IncomingFileViewModel(path));
            }
        }

        _lastBatch.Clear();
        UndoCommand.RaiseCanExecuteChanged();

        foreach (var destination in Destinations)
            destination.Refresh();

        StatusMessage = restored > 0 ? $"Put {restored} file(s) back" : "Nothing to undo";
        RaiseGridState();
        _ = LoadThumbnailsAsync();
    }

    private void Reload()
    {
        ErrorMessage = null;
        foreach (var d in Destinations)
            d.Refresh();
        Destinations.Clear();

        var root = BotWorkspace.Probe(_settings.BotRoot);
        if (root is null)
        {
            _workspace = null;
            ErrorMessage = "Could not find the posting bot. Pick its folder.";
            RaiseWorkspaceState();
            return;
        }

        _workspace = new BotWorkspace(root);
        if (!_workspace.LooksValid)
        {
            ErrorMessage = $"{root} has no bot.py / config.json.";
            RaiseWorkspaceState();
            return;
        }

        try
        {
            var config = _workspace.LoadConfig();
            var index = 1;
            foreach (var group in config.Groups)
                Destinations.Add(new DestinationViewModel(group, _workspace, index++));

            // Driest first, so the group that needs feeding is the one under your thumb.
            var ordered = Destinations.OrderBy(d => d.Days ?? double.MaxValue).ToList();
            Destinations.Clear();
            var position = 1;
            foreach (var d in ordered)
                Destinations.Add(new DestinationViewModel(d.Group, _workspace, position++));

            _host.Log($"Loaded {Destinations.Count} destination(s) from {_workspace.ConfigPath}");
            NotifyIfStarving();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"config.json could not be read: {ex.Message}";
        }

        RaiseWorkspaceState();
    }

    /// <summary>
    /// A dry group is not an error anyone sees from outside, since the bot quietly starts
    /// re-posting the archive. Worth saying out loud.
    /// </summary>
    private void NotifyIfStarving()
    {
        var dry = Destinations.Where(d => d.IsDry).Select(d => d.Name).ToList();
        if (dry.Count == 0)
        {
            _host.Notifications.ClearCondition("dry-groups");
            return;
        }

        _host.Notifications.SetCondition(
            "dry-groups",
            NotificationSeverity.Warning,
            dry.Count == 1 ? "1 group has run dry" : $"{dry.Count} groups have run dry",
            $"{string.Join(", ", dry)}. The bot will re-post from the archive instead of new material.");
    }

    private async Task LoadThumbnailsAsync()
    {
        CancelThumbnails();
        _thumbnailCts = new CancellationTokenSource();
        var token = _thumbnailCts.Token;

        try
        {
            foreach (var file in Incoming.ToList())
            {
                if (token.IsCancellationRequested)
                    return;
                await file.LoadThumbnailAsync(ThumbnailWidth, token);
            }
        }
        catch (OperationCanceledException)
        {
            // Opening another folder mid-load. Expected.
        }
    }

    private async Task LoadPreviewAsync(IncomingFileViewModel? file)
    {
        if (file is null)
        {
            PreviewImage = null;
            return;
        }

        var path = file.Path;
        var bitmap = await Task.Run(() =>
        {
            try
            {
                if (MediaRules.IsComic(path))
                {
                    var cover = MediaRules.ComicCover(path);
                    if (cover is null)
                        return null;
                    using var coverStream = new MemoryStream(cover);
                    return Bitmap.DecodeToWidth(coverStream, PreviewWidth);
                }

                if (!MediaRules.IsRenderableImage(path))
                    return null;

                using var stream = MediaRules.OpenShared(path);
                return Bitmap.DecodeToWidth(stream, PreviewWidth);
            }
            catch (Exception)
            {
                return null;
            }
        }).ConfigureAwait(true);

        if (Selected?.Path != path)
        {
            bitmap?.Dispose();
            return;
        }

        PreviewImage = bitmap;
    }

    private void OpenInExplorer(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not open {path}: {ex.Message}";
        }
    }

    private void ClearIncoming()
    {
        foreach (var file in Incoming)
            file.Dispose();
        Incoming.Clear();
        SetSelection([]);
        Selected = null;
        PreviewImage = null;
        RaiseGridState();
    }

    private void CancelThumbnails()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
    }

    private void RaiseGridState()
    {
        OnPropertyChanged(nameof(RemainingText));
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(SummaryText));
    }

    private void RaiseWorkspaceState()
    {
        OnPropertyChanged(nameof(HasWorkspace));
        OnPropertyChanged(nameof(BotRootText));
        OnPropertyChanged(nameof(SummaryText));
    }

    private void SaveSettings()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log($"Could not save Kibble settings: {ex.Message}");
        }
    }

    public void Dispose()
    {
        CancelThumbnails();
        PreviewImage = null;
        foreach (var file in Incoming)
            file.Dispose();
    }
}
