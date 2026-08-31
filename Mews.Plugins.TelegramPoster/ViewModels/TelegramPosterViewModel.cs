using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Mews.Plugins.Abstractions;
using Mews.Plugins.TelegramPoster.Model;
using Mews.Plugins.TelegramPoster.Services;
using Mews.Bot;

namespace Mews.Plugins.TelegramPoster.ViewModels;

public sealed class TelegramPosterViewModel : ObservableObject, IDisposable
{
    private const int ThumbnailWidth = 150;
    private const int PreviewWidth = 720;

    /// <summary>An archive can be thousands of files. Decoding them all is not worth the memory.</summary>
    private const int ThumbnailBudget = 300;

    private readonly IMewsHost _host;
    private readonly BotProcess _bot = new();
    private TelegramPosterSettings _settings;
    private BotWorkspace? _workspace;
    private CancellationTokenSource? _thumbnailCts;

    private GroupViewModel? _selectedGroup;
    private MediaItemViewModel? _selectedMedia;
    private MediaItemViewModel? _detailItem;
    private Bitmap? _previewImage;
    private bool _showArchive;
    private bool _isBotRunning;
    private string _statusMessage = "";
    private string? _errorMessage;
    private NextUp? _nextUp;
    private bool _isValidating;
    private ToolProbeResult _python = ToolProbeResult.Missing;
    private ToolProbeResult _git = ToolProbeResult.Missing;
    private bool _toolsChecked;

    public TelegramPosterViewModel(IMewsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<TelegramPosterSettings>() ?? new TelegramPosterSettings();

        ReloadCommand = new RelayCommand(Reload);
        RefreshMediaCommand = new RelayCommand(RefreshMedia);
        SaveGroupCommand = new RelayCommand(SaveGroup, () => SelectedGroup?.IsDirty == true);
        RevertGroupCommand = new RelayCommand(() => SelectedGroup?.Revert(), () => SelectedGroup?.IsDirty == true);
        OpenQueueFolderCommand = new RelayCommand(() => OpenInExplorer(SelectedGroup?.ToSendFolder));
        OpenArchiveFolderCommand = new RelayCommand(() => OpenInExplorer(SelectedGroup?.AlreadySentFolder));
        ShowQueueCommand = new RelayCommand(() => ShowArchive = false);
        ShowArchiveCommand = new RelayCommand(() => ShowArchive = true);
        StartBotCommand = new RelayCommand(StartBot, () => !IsBotRunning && _workspace?.LooksValid == true);
        StopBotCommand = new RelayCommand(StopBot, () => IsBotRunning);
        ClearSelectionCommand = new RelayCommand(() => SelectedMedia = null);
        RecheckToolsCommand = new RelayCommand(CheckTools);

        Setup = new BotSetupViewModel(
            log: line => _host.Log(line),
            onBotRootReady: SetBotRoot,
            currentWorkspace: () => _workspace,
            pythonPath: () => ResolvedPython,
            onRepositoryUrlChanged: PersistRepositoryUrl)
        {
            RepositoryUrl = _settings.RepositoryUrl ?? BotSetup.DefaultRepositoryUrl,
        };

        _bot.OutputReceived += line => _host.Log(line);
        _bot.Exited += code => Dispatcher.UIThread.Post(() =>
        {
            var wasRunning = IsBotRunning;
            IsBotRunning = false;
            if (!wasRunning)
                return;
            _host.Notifications.Post(
                code == 0 ? NotificationSeverity.Warning : NotificationSeverity.Error,
                "The posting bot stopped",
                $"It exited with code {code} without being asked to. Nothing is being posted.",
                new NotificationAction("Start again", StartBot));
        });

        Reload();
        CheckTools();
    }

    public BotSetupViewModel Setup { get; }

    public ObservableCollection<GroupViewModel> Groups { get; } = new();

    public ObservableCollection<MediaItemViewModel> MediaItems { get; } = new();

    public ObservableCollection<MediaItemViewModel> NextUpItems { get; } = new();

    public RelayCommand ReloadCommand { get; }

    public RelayCommand RefreshMediaCommand { get; }

    public RelayCommand SaveGroupCommand { get; }

    public RelayCommand RevertGroupCommand { get; }

    public RelayCommand OpenQueueFolderCommand { get; }

    public RelayCommand OpenArchiveFolderCommand { get; }

    public RelayCommand ShowQueueCommand { get; }

    public RelayCommand ShowArchiveCommand { get; }

    public RelayCommand StartBotCommand { get; }

    public RelayCommand StopBotCommand { get; }

    public RelayCommand ClearSelectionCommand { get; }

    public RelayCommand RecheckToolsCommand { get; }

    /// <summary>Saved interpreter, else whatever the probe found, else a guess.</summary>
    public string ResolvedPython => !string.IsNullOrWhiteSpace(_settings.PythonPath)
        ? _settings.PythonPath!
        : _python.Command ?? BotProcess.DefaultPythonCandidates[0];

    /// <summary>False until the probe comes back, so nothing flashes a warning on startup.</summary>
    public bool IsPythonMissing => _toolsChecked && !_python.Found;

    public bool IsGitMissing => _toolsChecked && !_git.Found;

    public bool IsAnyToolMissing => IsPythonMissing || IsGitMissing;

    public string ToolStatusText
    {
        get
        {
            if (!_toolsChecked)
                return "Checking for Python and git…";
            var parts = new List<string>();
            parts.Add(_python.Found ? $"Python: {_python.Version} ({_python.Command})" : "Python: not found");
            parts.Add(_git.Found ? $"git: {_git.Version}" : "git: not found");
            return string.Join("   ·   ", parts);
        }
    }

    /// <summary>Says what stops working, not just that something is missing.</summary>
    public string ToolWarningText
    {
        get
        {
            var parts = new List<string>();
            if (IsPythonMissing)
                parts.Add("Python was not found on PATH, so the bot cannot be started or have its dependencies installed. " +
                          "Install it from python.org and tick \"Add python.exe to PATH\".");
            if (IsGitMissing)
                parts.Add("git was not found on PATH, so the plugin cannot clone the bot for you.");
            return string.Join(" ", parts);
        }
    }

    public string BotRootText => _workspace?.Root ?? "No bot folder selected";

    public bool HasWorkspace => _workspace?.LooksValid == true;

    /// <summary>No usable checkout, so show setup instead of the browser.</summary>
    public bool ShowSetup => !HasWorkspace;

    /// <summary>Checkout is fine but there is no token yet.</summary>
    public bool NeedsToken => HasWorkspace && !HasToken;

    public bool HasToken => _workspace?.HasToken == true;

    public string ScheduledSummaryText
    {
        get
        {
            if (Groups.Count == 0)
                return "";
            var scheduled = Groups.Count(g => g.IsEnabled);
            return scheduled == Groups.Count
                ? $"{Groups.Count} group(s), all scheduled"
                : $"{scheduled} of {Groups.Count} group(s) scheduled";
        }
    }

    public bool HasGroupIssues => Groups.Any(g => g.HasIssues);

    public bool HasGroupErrors => Groups.Any(g => g.HasErrors);

    /// <summary>Name them. A count on its own does not tell you where to look.</summary>
    public string GroupIssueSummary
    {
        get
        {
            var broken = Groups.Where(g => g.HasErrors).Select(g => Describe(g)).ToList();
            var warned = Groups.Where(g => g.HasWarningsOnly).Select(g => Describe(g)).ToList();

            var parts = new List<string>();
            if (broken.Count > 0)
                parts.Add($"{broken.Count} group(s) will stop the bot from starting: {string.Join(", ", broken)}");
            if (warned.Count > 0)
                parts.Add($"{warned.Count} group(s) need a look: {string.Join(", ", warned)}");
            return string.Join("   ·   ", parts);
        }
    }

    private static string Describe(GroupViewModel group) =>
        string.IsNullOrWhiteSpace(group.Name) ? "(unnamed)" : group.Name;

    public string TokenStatusText => !HasWorkspace
        // No checkout means no .env to have an opinion about.
        ? ""
        : HasToken
            ? "BOT_TOKEN found in .env"
            : "No BOT_TOKEN in .env, so the bot will refuse to start";

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

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public bool IsBotRunning
    {
        get => _isBotRunning;
        private set
        {
            if (!SetField(ref _isBotRunning, value))
                return;
            OnPropertyChanged(nameof(BotStatusText));
            StartBotCommand.RaiseCanExecuteChanged();
            StopBotCommand.RaiseCanExecuteChanged();
        }
    }

    public string BotStatusText => IsBotRunning
        ? $"Running (pid {_bot.ProcessId?.ToString() ?? "?"})"
        : "Stopped";

    public GroupViewModel? SelectedGroup
    {
        get => _selectedGroup;
        set
        {
            if (!SetField(ref _selectedGroup, value))
                return;
            SelectedMedia = null;
            RefreshMedia();
            OnGroupDirtyChanged();
            OnPropertyChanged(nameof(NextUpMessage));
            OnPropertyChanged(nameof(HasNextUpMessage));
        }
    }

    public MediaItemViewModel? SelectedMedia
    {
        get => _selectedMedia;
        set
        {
            if (!SetField(ref _selectedMedia, value))
                return;
            OnPropertyChanged(nameof(HasSelection));
            UpdateDetail();
        }
    }

    /// <summary>False means the right column is showing next-up, not something you clicked.</summary>
    public bool HasSelection => _selectedMedia is not null;

    /// <summary>Whatever the right column is describing. Falls back to next-up.</summary>
    public MediaItemViewModel? DetailItem
    {
        get => _detailItem;
        private set
        {
            if (!SetField(ref _detailItem, value))
                return;
            OnPropertyChanged(nameof(HasDetail));
            OnPropertyChanged(nameof(DetailHeader));
            OnPropertyChanged(nameof(DetailComicText));
            OnPropertyChanged(nameof(HasDetailComicText));
        }
    }

    public bool HasDetail => DetailItem is not null;

    public string DetailHeader => SelectedMedia is not null ? "Selected" : "Next up";

    /// <summary>Albums cap at ten, so the page count tells you how many messages this becomes.</summary>
    public string DetailComicText
    {
        get
        {
            if (DetailItem is not { IsComic: true } item)
                return "";
            var pages = MediaRules.ComicPages(item.Path, SelectedGroup?.ComicOrder ?? "name").Count;
            if (pages == 0)
                return "No postable pages found in this archive.";
            var batches = (pages + MediaRules.MediaGroupLimit - 1) / MediaRules.MediaGroupLimit;
            return $"{pages} pages · posts as {batches} album{(batches == 1 ? "" : "s")}";
        }
    }

    public bool HasDetailComicText => DetailComicText.Length > 0;

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

    public bool ShowArchive
    {
        get => _showArchive;
        set
        {
            if (!SetField(ref _showArchive, value))
                return;
            OnPropertyChanged(nameof(ShowQueue));
            OnPropertyChanged(nameof(MediaHeaderText));
            SelectedMedia = null;
            RefreshMedia();
        }
    }

    public bool ShowQueue => !_showArchive;

    public string MediaHeaderText => ShowArchive ? "Already_Sent" : "To_Send";

    public string MediaCountText => MediaItems.Count == 1 ? "1 file" : $"{MediaItems.Count} files";

    /// <summary>What the bot will do next, including the cases where there is no answer.</summary>
    public string NextUpMessage => SelectedGroup is { IsEnabled: false }
        ? "This group is disabled, so the bot skips it entirely. The queue is what it would send once re-enabled."
        : _nextUp?.Kind switch
    {
        NextUpKind.Known => _nextUp.FilesPerPost > 1
            ? $"{_nextUp.Files.Count} file(s) go out together on the next run."
            : "",
        NextUpKind.RandomAtPostTime => "post_order is random, so the bot draws its file at post time. Nothing to preview.",
        NextUpKind.FallbackRandom => "To_Send is empty. The bot will re-post a random file from Already_Sent and leave it there.",
        // The validator covers this one, and it drives the list glyph too.
        NextUpKind.Nothing => "",
        _ => "",
    };

    public bool HasNextUpMessage => NextUpMessage.Length > 0;

    public void SetBotRoot(string path)
    {
        _settings.BotRoot = path;
        _host.SaveSettings(_settings);
        Reload();
    }

    private void Reload()
    {
        ErrorMessage = null;

        var root = BotWorkspace.Probe(_settings.BotRoot);
        if (root is null)
        {
            _workspace = null;
            Groups.Clear();
            MediaItems.Clear();
            NextUpItems.Clear();
            ErrorMessage = "Could not find the bot. Pick the telegram-posting-bot folder.";
            RaiseWorkspaceChanged();
            return;
        }

        _workspace = new BotWorkspace(root);
        RaiseWorkspaceChanged();

        if (!_workspace.LooksValid)
        {
            ErrorMessage = $"{root} has no bot.py / config.json.";
            return;
        }

        BotConfig config;
        try
        {
            config = _workspace.LoadConfig();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"config.json could not be read: {ex.Message}";
            return;
        }

        var previouslySelected = SelectedGroup?.ChatId;

        foreach (var group in Groups)
            group.PropertyChanged -= OnGroupPropertyChanged;

        Groups.Clear();
        foreach (var group in config.Groups)
        {
            var vm = new GroupViewModel(group, _workspace, OnGroupDirtyChanged);
            vm.PropertyChanged += OnGroupPropertyChanged;
            Groups.Add(vm);
        }

        SelectedGroup = Groups.FirstOrDefault(g => g.ChatId == previouslySelected) ?? Groups.FirstOrDefault();
        StatusMessage = "Loaded config.json";
        OnPropertyChanged(nameof(ScheduledSummaryText));
        ValidateGroups();
        _host.Log($"Loaded {Groups.Count} group(s) from {_workspace.ConfigPath}");
    }

    private void RefreshMedia()
    {
        CancelThumbnails();

        foreach (var item in MediaItems)
            item.Dispose();
        MediaItems.Clear();

        foreach (var item in NextUpItems)
            item.Dispose();
        NextUpItems.Clear();

        _nextUp = null;

        if (_workspace is null || SelectedGroup is null)
        {
            RaiseMediaChanged();
            UpdateDetail();
            return;
        }

        SelectedGroup.RefreshCounts();

        var config = SelectedGroup.ToConfig();
        var folder = ShowArchive ? _workspace.AlreadySentFolder(config) : _workspace.ToSendFolder(config);
        foreach (var path in _workspace.Scan(folder, recursive: ShowArchive))
            MediaItems.Add(new MediaItemViewModel(path));

        _nextUp = _workspace.ResolveNextUp(config);
        foreach (var path in _nextUp.Files)
            NextUpItems.Add(new MediaItemViewModel(path));

        RaiseMediaChanged();
        UpdateDetail();
        ValidateGroups();
        _ = LoadThumbnailsAsync();
    }

    private async Task LoadThumbnailsAsync()
    {
        CancelThumbnails();
        _thumbnailCts = new CancellationTokenSource();
        var token = _thumbnailCts.Token;

        // Next-up first, since that is the thumbnail someone is actually waiting on.
        var queue = NextUpItems.Concat(MediaItems.Take(ThumbnailBudget)).ToList();

        try
        {
            foreach (var item in queue)
            {
                if (token.IsCancellationRequested)
                    return;
                await item.LoadThumbnailAsync(ThumbnailWidth, token).ConfigureAwait(true);
            }
        }
        catch (OperationCanceledException)
        {
            // Switching groups mid-load. Expected.
        }
    }

    private void CancelThumbnails()
    {
        _thumbnailCts?.Cancel();
        _thumbnailCts?.Dispose();
        _thumbnailCts = null;
    }

    private void UpdateDetail()
    {
        DetailItem = SelectedMedia ?? NextUpItems.FirstOrDefault();
        OnPropertyChanged(nameof(DetailHeader));
        _ = LoadPreviewAsync(DetailItem);
    }

    private async Task LoadPreviewAsync(MediaItemViewModel? item)
    {
        if (item is null)
        {
            PreviewImage = null;
            return;
        }

        var path = item.Path;
        var isComic = item.IsComic;
        var comicOrder = SelectedGroup?.ComicOrder ?? "name";

        var bitmap = await Task.Run(() =>
        {
            try
            {
                if (isComic)
                {
                    var cover = MediaRules.ComicCover(path, comicOrder);
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

        // Do not let a slow decode stomp on a newer selection.
        if (DetailItem?.Path != path)
        {
            bitmap?.Dispose();
            return;
        }

        PreviewImage = bitmap;
    }

    private void SaveGroup()
    {
        if (_workspace is null || SelectedGroup is null)
            return;

        try
        {
            // We rewrite the whole file, so send every group, not just the edited one.
            var config = new BotConfig { Groups = Groups.Select(g => g.ToConfig()).ToList() };
            _workspace.SaveConfig(config);
            foreach (var group in Groups)
                group.AcceptChanges();

            StatusMessage = "Saved config.json";
            _host.Log($"Wrote {_workspace.ConfigPath}");

            if (IsBotRunning)
                StatusMessage = "Saved config.json. Restart the bot for it to take effect";

            RefreshMedia();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not write config.json: {ex.Message}";
            _host.Log($"Saving config.json failed: {ex}");
        }
    }

    private void PersistRepositoryUrl(string value)
    {
        // Takes the value directly. This fires from the Setup object initializer, when the
        // Setup property is still null and reading it would throw.
        var url = value.Trim();
        if (_settings.RepositoryUrl == url)
            return;
        _settings.RepositoryUrl = url;
        _host.SaveSettings(_settings);
    }

    private void CheckTools() =>
        _host.Background.Run("Checking Python and git", async context =>
        {
            context.Report("Probing PATH…");
            var python = await ToolProbe.FindPythonAsync(_settings.PythonPath, context.Token);
            var git = await ToolProbe.FindGitAsync(context.Token);
            await Dispatcher.UIThread.InvokeAsync(() => ApplyToolStatus(python, git));
        });

    private void ApplyToolStatus(ToolProbeResult python, ToolProbeResult git)
    {
        _python = python;
        _git = git;
        _toolsChecked = true;

        _host.Log(ToolStatusText);

        // Goes to the shell, because a missing interpreter still matters when you are
        // looking at another tab.
        if (IsAnyToolMissing)
            _host.Notifications.SetCondition(
                "missing-tools",
                IsPythonMissing ? NotificationSeverity.Error : NotificationSeverity.Warning,
                IsPythonMissing ? "Python not found" : "git not found",
                ToolWarningText,
                new NotificationAction("Re-check", CheckTools));
        else
            _host.Notifications.ClearCondition("missing-tools");

        OnPropertyChanged(nameof(IsPythonMissing));
        OnPropertyChanged(nameof(IsGitMissing));
        OnPropertyChanged(nameof(IsAnyToolMissing));
        OnPropertyChanged(nameof(ToolStatusText));
        OnPropertyChanged(nameof(ToolWarningText));
        OnPropertyChanged(nameof(ResolvedPython));
        Setup.SetToolStatus(_python.Found, _git.Found, ToolStatusText);
    }

    private void OnGroupPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isValidating)
            return;

        if (e.PropertyName == nameof(GroupViewModel.IsEnabled))
        {
            OnPropertyChanged(nameof(ScheduledSummaryText));
            OnPropertyChanged(nameof(NextUpMessage));
            OnPropertyChanged(nameof(HasNextUpMessage));
        }

        // Editing one group can create or clear a clash for another, so redo the lot.
        ValidateGroups();
    }

    private void ValidateGroups()
    {
        if (_workspace is null || _isValidating)
            return;

        _isValidating = true;
        try
        {
            var configs = Groups.Select(g => g.ToConfig()).ToList();
            for (var i = 0; i < Groups.Count; i++)
            {
                var others = configs.Where((_, index) => index != i).ToList();
                Groups[i].SetIssues(GroupValidator.Validate(
                    configs[i], _workspace, others, Groups[i].QueueCount, Groups[i].ArchiveCount));
            }
        }
        finally
        {
            _isValidating = false;
        }

        OnPropertyChanged(nameof(HasGroupIssues));
        OnPropertyChanged(nameof(HasGroupErrors));
        OnPropertyChanged(nameof(GroupIssueSummary));
    }

    private void OnGroupDirtyChanged()
    {
        SaveGroupCommand.RaiseCanExecuteChanged();
        RevertGroupCommand.RaiseCanExecuteChanged();
    }

    private void StartBot()
    {
        if (_workspace is null)
            return;

        ErrorMessage = null;
        var candidates = string.IsNullOrWhiteSpace(_settings.PythonPath)
            ? BotProcess.DefaultPythonCandidates
            : [_settings.PythonPath!];

        foreach (var candidate in candidates)
        {
            try
            {
                _bot.Start(candidate, _workspace);
                IsBotRunning = true;
                StatusMessage = $"Bot started via {candidate}";
                if (!string.Equals(_settings.PythonPath, candidate, StringComparison.Ordinal))
                {
                    _settings.PythonPath = candidate;
                    _host.SaveSettings(_settings);
                }
                return;
            }
            catch (Exception ex)
            {
                _host.Log($"Could not start with '{candidate}': {ex.Message}");
            }
        }

        ErrorMessage = "Could not start python. Check that it is on PATH.";
    }

    private void StopBot()
    {
        _bot.Stop();
        IsBotRunning = false;
        StatusMessage = "Bot stopped";
    }

    private void OpenInExplorer(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            Directory.CreateDirectory(path);
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not open {path}: {ex.Message}";
        }
    }

    private void RaiseWorkspaceChanged()
    {
        OnPropertyChanged(nameof(BotRootText));
        OnPropertyChanged(nameof(HasWorkspace));
        OnPropertyChanged(nameof(ShowSetup));
        OnPropertyChanged(nameof(HasToken));
        OnPropertyChanged(nameof(NeedsToken));
        OnPropertyChanged(nameof(TokenStatusText));
        Setup.Refresh();
        StartBotCommand.RaiseCanExecuteChanged();
    }

    private void RaiseMediaChanged()
    {
        OnPropertyChanged(nameof(MediaCountText));
        OnPropertyChanged(nameof(NextUpMessage));
        OnPropertyChanged(nameof(HasNextUpMessage));
    }

    public void Dispose()
    {
        foreach (var group in Groups)
            group.PropertyChanged -= OnGroupPropertyChanged;

        CancelThumbnails();
        _bot.Dispose();
        PreviewImage = null;

        foreach (var item in MediaItems)
            item.Dispose();
        foreach (var item in NextUpItems)
            item.Dispose();
    }
}
