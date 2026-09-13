using System.Collections.ObjectModel;
using System.Net.Http;
using System.Text.Json;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Media;
using Meows.Plugins.Scruff.Services;

namespace Meows.Plugins.Scruff.ViewModels;

/// <summary>What Scruff remembers between runs. Nothing in here is a secret; those are sealed separately.</summary>
public sealed class ScruffSettings
{
    /// <summary>Where cleaned copies and hand-off files land.</summary>
    public string? OutputFolder { get; set; }

    /// <summary>Where files were last added from, so the picker opens somewhere useful.</summary>
    public string? LastFolder { get; set; }

    public List<string> EnabledTargets { get; set; } = ["bluesky"];

    public Rating Rating { get; set; }

    public string MastodonVisibility { get; set; } = "public";

    public int MastodonMaxCharacters { get; set; } = MastodonTarget.DefaultMaxCharacters;

    /// <summary>The account names, which are not secret and are shown on the cards.</summary>
    public string? BlueskyAccount { get; set; }

    public string? MastodonAccount { get; set; }

    public string RedditSubreddit { get; set; } = "";
}

/// <summary>
/// One choice in a dropdown, named by the language the window is in. The label is the shared
/// bindable string for its key, so the dropdown reads correctly the moment the language changes.
/// </summary>
public sealed class Choice(string key, string tag)
{
    public string Tag { get; } = tag;

    public TranslatedString Label { get; } = MeowsText.Entry(key);
}

public sealed class ScruffViewModel : ObservableObject, IDisposable, IHandoffTarget
{
    private static string DefaultOutput() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Scruffed");

    private readonly IMeowsHost _host;
    private readonly HttpClient _http;
    private readonly IMeowsSecrets _secrets;
    private readonly ScruffSettings _settings;
    private readonly BlueskyTarget _bluesky;
    private readonly MastodonTarget _mastodon;
    private readonly RedditTarget _reddit;
    private readonly LanguageWatch _language;
    private readonly CancellationTokenSource _closing = new();

    private FileViewModel? _selected;
    private Bitmap? _preview;
    private string _title = "";
    private string _text = "";
    private string _tagsText = "";
    private string? _status;
    private string? _errorMessage;
    private bool _isBusy;

    /// <summary>Text that is delegated to the view, which owns the clipboard.</summary>
    public Action<string>? CopyText { get; set; }

    public ScruffViewModel(IMeowsHost host) : this(host, null)
    {
    }

    /// <summary>The client is injectable so a test can drive this without a network.</summary>
    public ScruffViewModel(IMeowsHost host, HttpClient? http)
    {
        _host = host;
        _http = http ?? NewClient();
        _secrets = host.Secrets;
        _settings = host.LoadSettings<ScruffSettings>() ?? new ScruffSettings();
        _settings.OutputFolder ??= DefaultOutput();

        _bluesky = new BlueskyTarget(_http) { Login = LoadBluesky() };
        _mastodon = new MastodonTarget(_http)
        {
            Login = LoadMastodon(),
            Account = _settings.MastodonAccount,
            MaxCharacters = _settings.MastodonMaxCharacters,
            Visibility = _settings.MastodonVisibility,
        };
        _reddit = new RedditTarget { Subreddit = _settings.RedditSubreddit };

        IPostTarget[] all = [_bluesky, _mastodon, new FurAffinityTarget(), new XTarget(), new InstagramTarget(), _reddit];
        foreach (var target in all)
            Targets.Add(new TargetViewModel(target, _settings.EnabledTargets.Contains(target.Id), OnTargetsChanged));

        if (_bluesky.Login is { } bluesky)
            Targets[0].Account = "@" + (_settings.BlueskyAccount ?? bluesky.Identifier);

        RemoveFileCommand = new RelayCommand(p => RemoveFile(p as FileViewModel));
        ClearFilesCommand = new RelayCommand(ClearFiles, () => Files.Count > 0 && !IsBusy);
        CleanToFolderCommand = new RelayCommand(() => _ = CleanAsync(inPlace: false), () => Files.Count > 0 && !IsBusy);
        CleanInPlaceCommand = new RelayCommand(() => _ = CleanAsync(inPlace: true), () => Files.Count > 0 && !IsBusy);
        PostCommand = new RelayCommand(() => _ = PostAsync(), () => !IsBusy && Targets.Any(t => t.IsReady));
        OpenOutputCommand = new RelayCommand(() => OpenLink(OutputFolder));
        SignInCommand = new RelayCommand(p => _ = SignInAsync(p as TargetViewModel));
        ForgetCommand = new RelayCommand(p => Forget(p as TargetViewModel));
        OpenOutcomeCommand = new RelayCommand(p => OpenLink((p as TargetViewModel)?.OutcomeUrl));
        OpenHandoffPageCommand = new RelayCommand(p => OpenLink((p as TargetViewModel)?.HandoffUrl));
        OpenHandoffFolderCommand = new RelayCommand(p => OpenLink((p as TargetViewModel)?.HandoffFolder));
        CopyCommand = new RelayCommand(p => CopyText?.Invoke((p as SheetFieldViewModel)?.Value ?? ""));

        _language = new LanguageWatch(() =>
        {
            OnEverythingChanged();
            foreach (var target in Targets)
                target.Reread();
            Recompose();
        });

        // Skia is borrowed from the shell rather than shipped, so say once where it came from.
        // If that ever stops resolving, this line is what fails, at activation, in the log.
        host.Log($"Scruff draws with SkiaSharp {SkiaSharp.SkiaSharpVersion.Native} from {typeof(SkiaSharp.SKBitmap).Assembly.Location}");

        Recompose();
    }

    private static HttpClient NewClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("Meows-Scruff/1.0 (+https://github.com/Zeralius/Meows)");
        return client;
    }

    // ---- Bound state ---------------------------------------------------------------------------

    public ObservableCollection<FileViewModel> Files { get; } = [];

    public ObservableCollection<TargetViewModel> Targets { get; } = [];

    public RelayCommand RemoveFileCommand { get; }

    public RelayCommand ClearFilesCommand { get; }

    public RelayCommand CleanToFolderCommand { get; }

    public RelayCommand CleanInPlaceCommand { get; }

    public RelayCommand PostCommand { get; }

    public RelayCommand OpenOutputCommand { get; }

    public RelayCommand SignInCommand { get; }

    public RelayCommand ForgetCommand { get; }

    public RelayCommand OpenOutcomeCommand { get; }

    public RelayCommand OpenHandoffPageCommand { get; }

    public RelayCommand OpenHandoffFolderCommand { get; }

    public RelayCommand CopyCommand { get; }

    public string OutputFolder => _settings.OutputFolder ?? DefaultOutput();

    public string? LastFolder => _settings.LastFolder;

    public bool IsEmpty => Files.Count == 0;

    public string CountText => Files.Count switch
    {
        0 => MeowsText.Current["scruff.count.none"],
        1 => MeowsText.Current["scruff.count.one"],
        var n => MeowsText.Current.Format("scruff.count.many", n),
    };

    public string Title
    {
        get => _title;
        set
        {
            if (SetField(ref _title, value ?? ""))
                Recompose();
        }
    }

    public string Text
    {
        get => _text;
        set
        {
            if (SetField(ref _text, value ?? ""))
                Recompose();
        }
    }

    public string TagsText
    {
        get => _tagsText;
        set
        {
            if (SetField(ref _tagsText, value ?? ""))
                Recompose();
        }
    }

    public IReadOnlyList<Choice> Ratings { get; } =
    [
        new("scruff.rating.general", nameof(Rating.General)),
        new("scruff.rating.mature", nameof(Rating.Mature)),
        new("scruff.rating.adult", nameof(Rating.Adult)),
    ];

    public Choice SelectedRating
    {
        get => Ratings.FirstOrDefault(r => r.Tag == _settings.Rating.ToString()) ?? Ratings[0];
        set
        {
            if (value is null || !Enum.TryParse<Rating>(value.Tag, out var rating) || _settings.Rating == rating)
                return;
            _settings.Rating = rating;
            Save();
            OnPropertyChanged();
            Recompose();
        }
    }

    public IReadOnlyList<Choice> Visibilities { get; } =
    [
        new("scruff.visibility.public", "public"),
        new("scruff.visibility.unlisted", "unlisted"),
        new("scruff.visibility.private", "private"),
    ];

    public Choice SelectedVisibility
    {
        get => Visibilities.FirstOrDefault(v => v.Tag == _settings.MastodonVisibility) ?? Visibilities[0];
        set
        {
            if (value is null || _settings.MastodonVisibility == value.Tag)
                return;
            _settings.MastodonVisibility = value.Tag;
            _mastodon.Visibility = value.Tag;
            Save();
            OnPropertyChanged();
        }
    }

    public string Subreddit
    {
        get => _settings.RedditSubreddit;
        set
        {
            if (_settings.RedditSubreddit == (value ?? ""))
                return;
            _settings.RedditSubreddit = value ?? "";
            _reddit.Subreddit = _settings.RedditSubreddit;
            Save();
            OnPropertyChanged();
        }
    }

    public FileViewModel? Selected
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

    public string Status
    {
        get => _status ?? MeowsText.Current["scruff.status.start"];
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
            RaiseCommands();
        }
    }

    private void RaiseCommands()
    {
        ClearFilesCommand.RaiseCanExecuteChanged();
        CleanToFolderCommand.RaiseCanExecuteChanged();
        CleanInPlaceCommand.RaiseCanExecuteChanged();
        PostCommand.RaiseCanExecuteChanged();
    }

    // ---- Files ---------------------------------------------------------------------------------

    public void SetOutputFolder(string folder)
    {
        _settings.OutputFolder = folder;
        Save();
        OnPropertyChanged(nameof(OutputFolder));
    }

    // ---- Handed files by another plugin ---------------------------------------------------------

    /// <summary>Files or a folder, from Kibble usually: onto the pile, the same as a drop.</summary>
    public bool Accepts(Handoff handoff) =>
        handoff.Verb is HandoffVerbs.Files or HandoffVerbs.Folder && handoff.Paths.Count > 0;

    public void Receive(Handoff handoff)
    {
        if (Accepts(handoff))
            AddPaths(handoff.Paths);
    }

    /// <summary>Puts files on the pile and starts reading them. A folder means everything in it, one level.</summary>
    public void AddPaths(IEnumerable<string> paths)
    {
        var added = new List<FileViewModel>();
        foreach (var path in paths)
        {
            if (Directory.Exists(path))
            {
                foreach (var inside in Directory.EnumerateFiles(path).OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                    Add(inside, added);
                _settings.LastFolder = path;
            }
            else if (File.Exists(path))
            {
                Add(path, added);
                _settings.LastFolder = Path.GetDirectoryName(path);
            }
        }

        if (added.Count == 0)
            return;

        Save();
        foreach (var target in Targets)
            target.ClearOutcome();

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
        RaiseCommands();
        Selected ??= added[0];
        _ = ReadAsync(added);
    }

    private void Add(string path, List<FileViewModel> added)
    {
        if (Files.Any(f => string.Equals(f.Path, path, StringComparison.OrdinalIgnoreCase)))
            return;

        var file = new FileViewModel(path);
        Files.Add(file);
        added.Add(file);
    }

    private void RemoveFile(FileViewModel? file)
    {
        if (file is null || !Files.Remove(file))
            return;

        if (ReferenceEquals(_selected, file))
            Selected = Files.FirstOrDefault();

        file.Dispose();
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
        RaiseCommands();
        Recompose();
    }

    private void ClearFiles()
    {
        Selected = null;
        foreach (var file in Files)
            file.Dispose();
        Files.Clear();

        foreach (var target in Targets)
            target.ClearOutcome();

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(CountText));
        RaiseCommands();
        Recompose();
    }

    /// <summary>
    /// Reads and cleans each new file off the UI thread, then shows what was found.
    ///
    /// Cleaning happens on arrival rather than on demand because the thing worth seeing first
    /// is the badge saying a file carries GPS, and the thumbnail is made from the cleaned copy
    /// so a phone photo appears the right way up.
    /// </summary>
    private async Task ReadAsync(IReadOnlyList<FileViewModel> files)
    {
        var token = _closing.Token;
        foreach (var file in files)
        {
            if (token.IsCancellationRequested)
                return;

            try
            {
                var (report, clean, thumbnail) = await Task.Run(() =>
                {
                    var bytes = File.ReadAllBytes(file.Path);
                    var report = Metadata.Inspect(bytes);
                    var clean = Preparer.Clean(bytes);
                    // A tile with no picture on it still says what the file carries.
                    var thumbnail = clean.Format != ImageFormat.Unknown ? Thumbnails.FromBytes(clean.Bytes, 160) : null;

                    return (report, clean, thumbnail);
                }, token);

                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    file.Report = report;
                    file.Clean = clean;
                    file.Thumbnail = thumbnail;
                    if (ReferenceEquals(_selected, file))
                        _ = ShowPreviewAsync(file);
                });
            }
            catch (OperationCanceledException)
            {
                return;
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() => file.Failure = ex.Message);
                _host.Log($"Scruff could not read {file.Path}: {ex.Message}");
            }
        }

        await Dispatcher.UIThread.InvokeAsync(Recompose);
    }

    private async Task ShowPreviewAsync(FileViewModel? file)
    {
        Preview = null;
        if (file?.Clean is not { } clean || clean.Format == ImageFormat.Unknown)
            return;

        try
        {
            var bitmap = await Task.Run(() => Thumbnails.FromBytes(clean.Bytes, 900));

            if (ReferenceEquals(_selected, file))
                Preview = bitmap;
            else
                bitmap?.Dispose();
        }
        catch (Exception)
        {
            // The tile still shows what it has.
        }
    }

    // ---- The draft -----------------------------------------------------------------------------

    public Draft CurrentDraft() => new()
    {
        Title = _title,
        Text = _text,
        Tags = Tags.Parse(_tagsText),
        Rating = _settings.Rating,
    };

    /// <summary>The pictures as they stand, for the previews. Only files that have been read.</summary>
    private List<Outgoing> CleanImages() =>
        Files.Where(f => f.Clean is not null)
            .Select(f => new Outgoing(f.CleanName, f.Clean!, f.Alt))
            .ToList();

    private void Recompose()
    {
        var draft = CurrentDraft();
        var images = CleanImages();
        foreach (var target in Targets)
            target.Recompose(draft, images, _host.Text);
        PostCommand.RaiseCanExecuteChanged();
    }

    private void OnTargetsChanged()
    {
        _settings.EnabledTargets = Targets.Where(t => t.IsEnabled).Select(t => t.Id).ToList();
        Save();
        PostCommand.RaiseCanExecuteChanged();
    }

    // ---- Cleaning ------------------------------------------------------------------------------

    /// <summary>
    /// Writes cleaned copies. Into the output folder, or over the originals with the originals
    /// sent to the Recycle Bin first, so a wrong click is a restore rather than a loss.
    /// </summary>
    public async Task CleanAsync(bool inPlace)
    {
        if (IsBusy || Files.Count == 0)
            return;

        IsBusy = true;
        ErrorMessage = null;
        Status = _host.Text["scruff.status.cleaning"];

        var written = 0;
        var removed = 0;
        var failures = new List<string>();
        var files = Files.ToList();
        var output = OutputFolder;

        try
        {
            await Task.Run(() =>
            {
                if (!inPlace)
                    Directory.CreateDirectory(output);

                foreach (var file in files)
                {
                    try
                    {
                        var clean = file.Clean ?? Preparer.Clean(File.ReadAllBytes(file.Path));
                        if (clean.Format == ImageFormat.Unknown)
                        {
                            failures.Add(_host.Text.Format("scruff.error.notapicture", file.Name));
                            continue;
                        }

                        if (clean.Original.CarriesAnything || clean.Turned)
                            removed++;

                        var name = file.CleanName;
                        if (inPlace)
                        {
                            var folder = Path.GetDirectoryName(file.Path) ?? "";
                            var final = Path.Combine(folder, name);
                            var temp = Path.Combine(folder, "." + name + ".scruff");
                            File.WriteAllBytes(temp, clean.Bytes);

                            var outcome = RecycleBin.Send([file.Path]);
                            if (outcome.Failed > 0)
                            {
                                File.Delete(temp);
                                failures.Add($"{file.Name}: {outcome.FailureReason}");
                                continue;
                            }

                            File.Move(temp, final, overwrite: true);
                        }
                        else
                        {
                            File.WriteAllBytes(Unique(Path.Combine(output, name)), clean.Bytes);
                        }

                        written++;
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"{file.Name}: {ex.Message}");
                    }
                }
            }, _closing.Token);

            Status = _host.Text.Format("scruff.status.cleaned", written, removed);
            _host.Log($"Scruff cleaned {written} file(s), {removed} carried something, {failures.Count} failed.");
            _host.Notifications.Post(NotificationSeverity.Info, _host.Text["scruff.notify.cleaned"], Status);

            if (failures.Count > 0)
                ErrorMessage = string.Join("\n", failures);

            // Cleaned in place means the pile is now the cleaned files, so read them again.
            if (inPlace)
            {
                var paths = files.Select(f => Path.Combine(Path.GetDirectoryName(f.Path) ?? "", f.CleanName)).ToList();
                ClearFiles();
                AddPaths(paths);
            }
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            IsBusy = false;
        }
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path))
            return path;

        var folder = Path.GetDirectoryName(path) ?? "";
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){extension}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    // ---- Posting -------------------------------------------------------------------------------

    /// <summary>
    /// Sends the draft everywhere that is switched on and can take it.
    ///
    /// Each place gets its own fitted copies, made from the cleaned files, and each is tried
    /// whether or not the one before it succeeded, because a Mastodon server being down is no
    /// reason not to post to Bluesky. The places done by hand come last, since each of those
    /// opens a browser window.
    /// </summary>
    public async Task PostAsync()
    {
        if (IsBusy)
            return;

        var going = Targets.Where(t => t.IsReady).ToList();
        if (going.Count == 0)
            return;

        IsBusy = true;
        ErrorMessage = null;
        Status = _host.Text["scruff.status.posting"];

        var draft = CurrentDraft();
        var files = Files.ToList();
        var posted = 0;
        var failed = 0;
        var handed = 0;
        var token = _closing.Token;

        try
        {
            // Anything not read yet is read now, so nothing goes out with its EXIF still on.
            foreach (var file in files.Where(f => f.Clean is null))
            {
                try
                {
                    var bytes = await Task.Run(() => File.ReadAllBytes(file.Path), token);
                    file.Report = Metadata.Inspect(bytes);
                    file.Clean = await Task.Run(() => Preparer.Clean(bytes), token);
                }
                catch (Exception ex)
                {
                    file.Failure = ex.Message;
                }
            }

            var readable = files.Where(f => f.Clean is not null).ToList();

            foreach (var target in going.OrderBy(t => t.PostsItself ? 0 : 1))
            {
                token.ThrowIfCancellationRequested();
                target.ClearOutcome();
                target.IsBusy = true;

                try
                {
                    var limits = target.Target.Limits;
                    var fitted = await Task.Run(() => readable.Select(f =>
                    {
                        var fit = Preparer.Fit(f.Clean!, limits);
                        return new Outgoing(Path.GetFileNameWithoutExtension(f.CleanName) + fit.Extension, fit, f.Alt);
                    }).ToList(), token);

                    target.Recompose(draft, fitted, _host.Text);
                    if (!target.CanGo)
                    {
                        target.Outcome = _host.Text["scruff.outcome.skipped"];
                        target.Failed = true;
                        failed++;
                        continue;
                    }

                    if (target.Target is IApiTarget api)
                    {
                        var result = await api.PostAsync(draft, fitted, token);
                        if (result.Ok)
                        {
                            posted++;
                            target.Outcome = _host.Text["scruff.outcome.posted"];
                            target.OutcomeUrl = result.Url;
                            _host.Log($"Scruff posted to {target.Name}: {result.Url ?? "(no link)"}");
                        }
                        else
                        {
                            failed++;
                            target.Outcome = result.Error ?? _host.Text["scruff.outcome.failed"];
                            target.Failed = true;
                            _host.Log($"Scruff could not post to {target.Name}: {result.Error}");
                        }
                    }
                    else if (target.Target is IManualTarget hand)
                    {
                        var folder = await Task.Run(() => WriteHandoff(target.Id, fitted), token);
                        var sheet = hand.Sheet(draft, fitted);
                        target.ShowSheet(sheet, _host.Text);
                        target.HandoffFolder = folder;
                        target.Outcome = _host.Text["scruff.outcome.handed"];
                        handed++;

                        OpenLink(folder);
                        OpenLink(sheet.Url);
                        _host.Log($"Scruff handed {fitted.Count} file(s) to {target.Name} in {folder}");
                    }
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception ex)
                {
                    failed++;
                    target.Outcome = ex.Message;
                    target.Failed = true;
                    _host.Log($"Scruff failed on {target.Name}: {ex}");
                }
                finally
                {
                    target.IsBusy = false;
                }
            }

            Status = _host.Text.Format("scruff.status.posted", posted, handed, failed);
            _host.Notifications.Post(
                failed > 0 ? NotificationSeverity.Warning : NotificationSeverity.Info,
                _host.Text["scruff.notify.posted"], Status);
        }
        catch (OperationCanceledException)
        {
            Status = "";
        }
        finally
        {
            IsBusy = false;
            // The previews go back to being about the cleaned files, not the last fit.
            Recompose();
        }
    }

    /// <summary>The fitted files for one place, in a folder of their own, dated so two hand-offs do not mix.</summary>
    private string WriteHandoff(string targetId, IReadOnlyList<Outgoing> images)
    {
        var folder = Path.Combine(OutputFolder, targetId, DateTime.Now.ToString("yyyy-MM-dd HHmm"));
        Directory.CreateDirectory(folder);
        foreach (var image in images)
            File.WriteAllBytes(Unique(Path.Combine(folder, image.FileName)), image.File.Bytes);
        return folder;
    }

    // ---- Accounts ------------------------------------------------------------------------------

    private sealed class StoredBluesky
    {
        public string Identifier { get; set; } = "";

        public string Password { get; set; } = "";
    }

    private sealed class StoredMastodon
    {
        public string Instance { get; set; } = "";

        public string Token { get; set; } = "";
    }

    private BlueskyLogin? LoadBluesky()
    {
        var stored = Read<StoredBluesky>("bluesky");
        return stored is { Identifier.Length: > 0, Password.Length: > 0 }
            ? new BlueskyLogin(stored.Identifier, stored.Password)
            : null;
    }

    private MastodonLogin? LoadMastodon()
    {
        var stored = Read<StoredMastodon>("mastodon");
        return stored is { Instance.Length: > 0, Token.Length: > 0 }
            ? new MastodonLogin(stored.Instance, stored.Token)
            : null;
    }

    private T? Read<T>(string name) where T : class
    {
        try
        {
            var json = _secrets.Get(name);
            return json is null ? null : JsonSerializer.Deserialize<T>(json);
        }
        catch (Exception ex)
        {
            _host.Log($"Scruff could not read the {name} credential: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Checks the login against the service and keeps it only if it works. The password or
    /// token is cleared from the box either way, so it is not left on screen.
    /// </summary>
    public async Task SignInAsync(TargetViewModel? card)
    {
        if (card is null || card.LoginA.Trim().Length == 0 || card.LoginB.Trim().Length == 0)
            return;

        card.IsBusy = true;
        ErrorMessage = null;
        try
        {
            if (card.Target is BlueskyTarget)
            {
                var login = new BlueskyLogin(card.LoginA.Trim().TrimStart('@'), card.LoginB.Trim());
                var handle = await _bluesky.VerifyAsync(login, _closing.Token);

                _secrets.Set("bluesky", JsonSerializer.Serialize(new StoredBluesky { Identifier = login.Identifier, Password = login.Password }));
                _bluesky.Login = login;
                _settings.BlueskyAccount = handle;
                card.Account = "@" + handle;
            }
            else if (card.Target is MastodonTarget)
            {
                var login = new MastodonLogin(card.LoginA.Trim(), card.LoginB.Trim());
                var (account, max) = await _mastodon.VerifyAsync(login, _closing.Token);

                _secrets.Set("mastodon", JsonSerializer.Serialize(new StoredMastodon { Instance = login.Instance, Token = login.Token }));
                _mastodon.Login = login;
                _mastodon.Account = account;
                _mastodon.MaxCharacters = max;
                _settings.MastodonAccount = account;
                _settings.MastodonMaxCharacters = max;
                card.Account = account;
            }

            Save();
            card.LoginB = "";
            Status = _host.Text.Format("scruff.status.signedin", card.Name, card.Account ?? "");
            _host.Log($"Scruff signed in to {card.Name} as {card.Account}");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            card.LoginB = "";
            ErrorMessage = _host.Text.Format("scruff.error.signin", card.Name, ex.Message);
            _host.Log($"Scruff could not sign in to {card.Name}: {ex.Message}");
        }
        finally
        {
            card.IsBusy = false;
            PostCommand.RaiseCanExecuteChanged();
        }
    }

    private void Forget(TargetViewModel? card)
    {
        if (card is null)
            return;

        if (card.Target is BlueskyTarget)
        {
            _secrets.Forget("bluesky");
            _bluesky.Login = null;
            _settings.BlueskyAccount = null;
        }
        else if (card.Target is MastodonTarget)
        {
            _secrets.Forget("mastodon");
            _mastodon.Login = null;
            _mastodon.Account = null;
            _settings.MastodonAccount = null;
        }

        Save();
        card.Account = null;
        PostCommand.RaiseCanExecuteChanged();
    }

    // ---- Odds and ends -------------------------------------------------------------------------

    private void OpenLink(string? target)
    {
        if (string.IsNullOrWhiteSpace(target))
            return;

        try
        {
            if (!target.StartsWith("http", StringComparison.OrdinalIgnoreCase))
                Directory.CreateDirectory(target);

            Explorer.Open(target);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("scruff.error.open", target, ex.Message);
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
            _host.Log($"Could not save Scruff settings: {ex.Message}");
        }
    }

    private bool _disposed;

    /// <summary>
    /// Twice, in practice: the shell disposes the view and then its DataContext, and the view
    /// disposes its DataContext itself. The second call has to be a no-op rather than a throw
    /// from a token source that is already gone.
    /// </summary>
    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;

        _language.Dispose();
        _closing.Cancel();

        foreach (var file in Files)
            file.Dispose();

        Preview = null;
        _http.Dispose();
        _closing.Dispose();
    }
}
