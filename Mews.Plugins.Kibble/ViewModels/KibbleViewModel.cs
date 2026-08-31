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
        SelectFileCommand = new RelayCommand(p => Selected = p as IncomingFileViewModel);
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

    public RelayCommand SelectFileCommand { get; }

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
            var previous = _selected;
            if (!SetField(ref _selected, value))
                return;
            if (previous is not null)
                previous.IsSelected = false;
            if (value is not null)
                value.IsSelected = true;
            OnPropertyChanged(nameof(HasSelection));
            BlockedReason = null;
            SendToCommand.RaiseCanExecuteChanged();
            SkipCommand.RaiseCanExecuteChanged();
            _ = LoadPreviewAsync(value);
        }
    }

    public bool HasSelection => _selected is not null;

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

        try
        {
            // Everything, not just what the bot can post. A file it cannot use is exactly
            // what you want to see and deal with, rather than have quietly hidden.
            var files = Directory.EnumerateFiles(folder)
                .OrderBy(p => Path.GetFileName(p), Comparer<string>.Create(MediaRules.CompareNatural));

            foreach (var file in files)
                Incoming.Add(new IncomingFileViewModel(file));
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

    /// <summary>Sends the selection to a destination, by click or by number key.</summary>
    private void SendTo(object? parameter)
    {
        if (_workspace is null || Selected is null)
            return;

        var destination = parameter as DestinationViewModel
                          ?? (parameter is int i ? Destinations.ElementAtOrDefault(i - 1) : null);
        if (destination is null)
            return;

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

    private void UndoLastBatch()
    {
        var restored = 0;
        foreach (var result in _lastBatch)
        {
            if (!Intake.Undo(result))
                continue;
            restored++;
            Incoming.Add(new IncomingFileViewModel(result.SourcePath));
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

                using var stream = File.OpenRead(path);
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
