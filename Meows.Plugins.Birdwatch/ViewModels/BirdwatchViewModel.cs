using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Birdwatch.Services;

namespace Meows.Plugins.Birdwatch.ViewModels;

/// <summary>What Birdwatch remembers between runs.</summary>
public sealed class BirdwatchSettings
{
    public List<string> Handles { get; set; } = [];

    public string? IntakeFolder { get; set; }

    public bool IncludeReposts { get; set; }

    /// <summary>How many tiles to build at once. A feed does not end.</summary>
    public int Batch { get; set; } = 60;
}

/// <summary>One account being watched, and how its last look went.</summary>
public sealed class WatchedViewModel(string handle) : ObservableObject
{
    private string _status = "";
    private bool _isBusy;

    public string Handle { get; } = handle;

    /// <summary>Where the next page starts. Null means back to the top.</summary>
    public string? Cursor { get; set; }

    public string Status
    {
        get => _status;
        set => SetField(ref _status, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }
}

/// <summary>
/// One picture, which is the unit the grid deals in rather than one post.
///
/// A post with four images is four things you might want and four things you might already have,
/// so it is four tiles. The post is still carried along, because the name a file gets and the
/// link back both come from it.
/// </summary>
public sealed class MediaViewModel : ObservableObject, IDisposable
{
    private Bitmap? _thumbnail;
    private bool _isSaved;
    private bool _isSaving;

    public MediaViewModel(FeedPost post, FeedMedia media, int index, bool alreadySaved)
    {
        Post = post;
        Media = media;
        Index = index;
        _isSaved = alreadySaved;
    }

    public FeedPost Post { get; }

    public FeedMedia Media { get; }

    public int Index { get; }

    public string AuthorHandle => Post.AuthorHandle;

    public string Alt => Media.Alt;

    public bool HasAlt => Media.Alt.Length > 0;

    public string WhenText => Post.PostedAt == DateTimeOffset.MinValue
        ? ""
        : Post.PostedAt.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string LabelsText => string.Join(", ", Post.Labels);

    public bool HasLabels => Post.Labels.Count > 0;

    public bool IsRepost => Post.IsRepost;

    public bool IsVideo => Media.Kind == MediaKind.Video;

    public bool CanSave => Media.CanSave && !_isSaved;

    public bool IsSaved
    {
        get => _isSaved;
        set
        {
            if (SetField(ref _isSaved, value))
                OnPropertyChanged(nameof(CanSave));
        }
    }

    public bool IsSaving
    {
        get => _isSaving;
        set => SetField(ref _isSaving, value);
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

    /// <summary>Whether a decode has been attempted, so a second pass does not repeat one.</summary>
    public bool Asked { get; set; }

    public void Dispose() => Thumbnail = null;
}

public sealed class BirdwatchViewModel : ObservableObject, IDisposable
{
    /// <summary>
    /// Shared with Saucer on purpose. Both end their job by dropping a file where Kibble will
    /// find it, and two plugins disagreeing about where that is would be its own small disaster.
    /// </summary>
    private static string DefaultIntake() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Kibble intake");

    private readonly IMeowsHost _host;
    private readonly HttpClient _http;
    private readonly IFeedSource _source;
    private readonly MediaSaver _saver;
    private BirdwatchSettings _settings;

    private readonly List<MediaViewModel> _all = [];
    private CancellationTokenSource? _work;
    private CancellationTokenSource? _thumbnails;

    private string _newHandle = "";
    private string _status = "";
    private string? _errorMessage;
    private bool _isBusy;
    private MediaViewModel? _selected;
    private Bitmap? _preview;
    private int _shown;

    public BirdwatchViewModel(IMeowsHost host) : this(host, null, null)
    {
    }

    /// <summary>
    /// The source and the client are injectable so a test can drive this without a network. The
    /// app always takes the defaults.
    /// </summary>
    public BirdwatchViewModel(IMeowsHost host, IFeedSource? source, HttpClient? http)
    {
        _host = host;
        _settings = host.LoadSettings<BirdwatchSettings>() ?? new BirdwatchSettings();
        _settings.IntakeFolder ??= DefaultIntake();

        _http = http ?? NewClient();
        _source = source ?? new BlueskyFeed(_http);
        _saver = new MediaSaver(_http);

        foreach (var handle in _settings.Handles)
            Watched.Add(new WatchedViewModel(handle));

        AddHandleCommand = new RelayCommand(AddHandle, () => NewHandle.Trim().Length > 0);
        RemoveHandleCommand = new RelayCommand(p => RemoveHandle(p as WatchedViewModel));
        RefreshCommand = new RelayCommand(() => _ = LoadAsync(more: false), () => !IsBusy && Watched.Count > 0);
        LoadMoreCommand = new RelayCommand(() => _ = LoadAsync(more: true), () => !IsBusy && HasMore);
        SaveCommand = new RelayCommand(p => _ = SaveAsync(p as MediaViewModel));
        SavePostCommand = new RelayCommand(p => _ = SavePostAsync(p as MediaViewModel));
        OpenPostCommand = new RelayCommand(p => OpenLink((p as MediaViewModel)?.Post.WebUrl));
        OpenIntakeCommand = new RelayCommand(() => OpenLink(IntakeFolder));

        Status = _host.Text["birdwatch.status.start"];
    }

    private static HttpClient NewClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };

        // Saying who is calling is the polite minimum for a public API, and it is what lets
        // anyone on the other end tell this apart from a scraper if they ever need to.
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Meows-Birdwatch/1.0 (+https://github.com/Zeralius/Meows)");
        return client;
    }

    public ObservableCollection<WatchedViewModel> Watched { get; } = [];

    public ObservableCollection<MediaViewModel> Shown { get; } = [];

    public RelayCommand AddHandleCommand { get; }

    public RelayCommand RemoveHandleCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand LoadMoreCommand { get; }

    public RelayCommand SaveCommand { get; }

    public RelayCommand SavePostCommand { get; }

    public RelayCommand OpenPostCommand { get; }

    public RelayCommand OpenIntakeCommand { get; }

    public string ServiceName => _source.ServiceName;

    public string NewHandle
    {
        get => _newHandle;
        set
        {
            if (SetField(ref _newHandle, value))
                AddHandleCommand.RaiseCanExecuteChanged();
        }
    }

    public string IntakeFolder => _settings.IntakeFolder ?? DefaultIntake();

    public bool IncludeReposts
    {
        get => _settings.IncludeReposts;
        set
        {
            if (_settings.IncludeReposts == value)
                return;
            _settings.IncludeReposts = value;
            Save();
            OnPropertyChanged();
            Rebuild();
        }
    }

    public string Status
    {
        get => _status;
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

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value))
                return;
            RefreshCommand.RaiseCanExecuteChanged();
            LoadMoreCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsEmpty => Shown.Count == 0;

    /// <summary>Whether any watched account has somewhere further to read from.</summary>
    public bool HasMore => Watched.Any(w => w.Cursor is { Length: > 0 });

    public MediaViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value))
                return;
            OnPropertyChanged(nameof(HasSelection));
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

    public void SetIntakeFolder(string folder)
    {
        _settings.IntakeFolder = folder;
        Save();
        OnPropertyChanged(nameof(IntakeFolder));
        Rebuild();
    }

    private void AddHandle()
    {
        var handle = BlueskyFeed.TidyHandle(NewHandle);
        if (handle.Length == 0 || Watched.Any(w => w.Handle == handle))
        {
            NewHandle = "";
            return;
        }

        Watched.Add(new WatchedViewModel(handle));
        _settings.Handles = Watched.Select(w => w.Handle).ToList();
        Save();

        NewHandle = "";
        RefreshCommand.RaiseCanExecuteChanged();
        _ = LoadAsync(more: false);
    }

    private void RemoveHandle(WatchedViewModel? watched)
    {
        if (watched is null || !Watched.Remove(watched))
            return;

        _settings.Handles = Watched.Select(w => w.Handle).ToList();
        Save();

        // Everything of theirs goes with them, rather than lingering until the next refresh.
        foreach (var gone in _all.Where(m => m.AuthorHandle == watched.Handle).ToList())
        {
            _all.Remove(gone);
            gone.Dispose();
        }

        RefreshCommand.RaiseCanExecuteChanged();
        Rebuild();
    }

    /// <summary>
    /// Reads every watched account and merges what comes back, newest first.
    ///
    /// One page each rather than one account at a time, because the interesting view is all of
    /// them together in the order things were posted.
    /// </summary>
    public async Task LoadAsync(bool more)
    {
        if (IsBusy || Watched.Count == 0)
            return;

        _work?.Cancel();
        _work = new CancellationTokenSource();
        var token = _work.Token;

        IsBusy = true;
        ErrorMessage = null;
        Status = _host.Text["birdwatch.status.reading"];

        var failures = new List<string>();
        var found = 0;

        try
        {
            foreach (var watched in Watched.ToList())
            {
                token.ThrowIfCancellationRequested();

                // Starting again means starting at the top, not where the last look stopped.
                if (!more)
                    watched.Cursor = null;
                else if (watched.Cursor is null)
                    continue;

                watched.IsBusy = true;
                try
                {
                    var page = await _source.FetchAsync(watched.Handle, watched.Cursor, token);
                    watched.Cursor = page.Cursor;
                    watched.Status = _host.Text.Format("birdwatch.watched.posts", page.Posts.Count);
                    found += Merge(page);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    // One account being wrong should not stop the others. A typo in a handle is
                    // the usual cause and the message says which one.
                    watched.Status = _host.Text["birdwatch.watched.failed"];
                    failures.Add($"{watched.Handle}: {Explain(ex)}");
                    _host.Log($"Birdwatch could not read {watched.Handle}: {ex.Message}");
                }
                finally
                {
                    watched.IsBusy = false;
                }
            }

            Rebuild();
            Status = _host.Text.Format("birdwatch.status.found", found, _all.Count);
            if (failures.Count > 0)
                ErrorMessage = string.Join("\n", failures);
        }
        catch (OperationCanceledException)
        {
            Status = "";
        }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(HasMore));
            LoadMoreCommand.RaiseCanExecuteChanged();
        }
    }

    private string Explain(Exception ex) => ex switch
    {
        HttpRequestException { StatusCode: System.Net.HttpStatusCode.BadRequest } =>
            _host.Text["birdwatch.error.nosuchaccount"],
        HttpRequestException => _host.Text["birdwatch.error.offline"],
        TaskCanceledException => _host.Text["birdwatch.error.slow"],
        _ => ex.Message,
    };

    /// <summary>Adds what is new. A refresh overlaps the last one heavily, and a repeat is not news.</summary>
    private int Merge(FeedPage page)
    {
        var added = 0;
        foreach (var post in page.Posts)
        {
            for (var i = 0; i < post.Media.Count; i++)
            {
                if (_all.Any(m => m.Post.Id == post.Id && m.Index == i))
                    continue;

                _all.Add(new MediaViewModel(post, post.Media[i], i,
                    MediaSaver.AlreadySaved(IntakeFolder, post, i) is not null));
                added++;
            }
        }

        return added;
    }

    /// <summary>
    /// Puts the merged set on screen, newest first, a batch at a time.
    ///
    /// The batching is Kibble's lesson rather than a guess: building a tile and decoding a
    /// picture for everything at once is what made a folder of thousands crawl, and a feed is
    /// worse than a folder because it grows while you are looking at it.
    /// </summary>
    private void Rebuild()
    {
        var wanted = _all
            .Where(m => IncludeReposts || !m.IsRepost)
            .OrderByDescending(m => m.Post.PostedAt)
            .ThenBy(m => m.Index)
            .Take(Math.Max(_shown, _settings.Batch))
            .ToList();

        Shown.Clear();
        foreach (var media in wanted)
            Shown.Add(media);

        _shown = Shown.Count;

        OnPropertyChanged(nameof(IsEmpty));
        _ = LoadThumbnailsAsync();
    }

    private async Task LoadThumbnailsAsync()
    {
        _thumbnails?.Cancel();
        _thumbnails = new CancellationTokenSource();
        var token = _thumbnails.Token;

        foreach (var tile in Shown.ToList())
        {
            if (token.IsCancellationRequested)
                return;

            if (tile.Asked || tile.Media.ThumbnailUrl.Length == 0)
                continue;

            tile.Asked = true;
            try
            {
                var bytes = await _http.GetByteArrayAsync(tile.Media.ThumbnailUrl, token);
                using var stream = new MemoryStream(bytes);
                var bitmap = new Bitmap(stream);
                await Dispatcher.UIThread.InvokeAsync(() => tile.Thumbnail = bitmap);
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception)
            {
                // A tile with no picture on it is still a tile you can save from. Not worth
                // saying anything about, and certainly not worth stopping the rest.
            }
        }
    }

    private async Task ShowPreviewAsync(MediaViewModel? media)
    {
        Preview = null;
        if (media is null)
            return;

        var url = media.Media.FullUrl ?? media.Media.ThumbnailUrl;
        if (url.Length == 0)
            return;

        try
        {
            var bytes = await _http.GetByteArrayAsync(url, CancellationToken.None);
            using var stream = new MemoryStream(bytes);
            var bitmap = new Bitmap(stream);

            // Still the one being looked at? Clicking through a grid quickly means several of
            // these are in flight, and the last to arrive is not necessarily the right one.
            if (ReferenceEquals(_selected, media))
                Preview = bitmap;
            else
                bitmap.Dispose();
        }
        catch (Exception)
        {
            // The tile still shows what it has.
        }
    }

    public async Task SaveAsync(MediaViewModel? media)
    {
        if (media is null || !media.Media.CanSave || media.IsSaving)
            return;

        media.IsSaving = true;
        try
        {
            var result = await _saver.SaveAsync(
                media.Post, media.Media, media.Index, IntakeFolder, CancellationToken.None);

            switch (result.Outcome)
            {
                case SaveOutcome.Saved:
                    media.IsSaved = true;
                    Status = _host.Text.Format("birdwatch.status.saved", Path.GetFileName(result.Path!));
                    _host.Log($"Birdwatch saved {result.Path}");
                    break;

                case SaveOutcome.AlreadyThere:
                    media.IsSaved = true;
                    Status = _host.Text["birdwatch.status.already"];
                    break;

                case SaveOutcome.Failed:
                    ErrorMessage = _host.Text.Format("birdwatch.error.save", result.Detail ?? "");
                    _host.Log($"Birdwatch could not save from {media.Post.Id}: {result.Detail}");
                    break;
            }
        }
        finally
        {
            media.IsSaving = false;
        }
    }

    /// <summary>Everything on the post this tile belongs to, for a set that only makes sense together.</summary>
    public async Task SavePostAsync(MediaViewModel? media)
    {
        if (media is null)
            return;

        foreach (var sibling in Shown.Where(m => m.Post.Id == media.Post.Id).ToList())
            await SaveAsync(sibling);
    }

    private void OpenLink(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return;

        try
        {
            if (!target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                Directory.CreateDirectory(target);

            Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("birdwatch.error.open", target, ex.Message);
        }
    }

    private void Save()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log($"Could not save Birdwatch settings: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _work?.Cancel();
        _thumbnails?.Cancel();

        foreach (var media in _all)
            media.Dispose();

        Preview = null;
        _http.Dispose();
    }
}
