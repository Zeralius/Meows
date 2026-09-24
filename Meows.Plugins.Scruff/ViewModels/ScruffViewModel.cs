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

    public string? DeviantArtAccount { get; set; }

    public string? TumblrAccount { get; set; }

    /// <summary>The blogs the Tumblr account had at sign in, primary first, and which gets the post.</summary>
    public List<string> TumblrBlogs { get; set; } = [];

    public string TumblrBlog { get; set; } = "";

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

/// <summary>One line in the strip above Post: which places, what about them, and whether it would stop them.</summary>
public sealed record BeforePostingLine(string Places, string Text, bool IsProblem);

public sealed class ScruffViewModel : ObservableObject, IDisposable, IHandoffTarget, ISearchable, IActionTarget
{
    private static string DefaultOutput() => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.MyPictures), "Scruffed");

    private readonly IMeowsHost _host;
    private readonly HttpClient _http;
    private readonly IMeowsSecrets _secrets;
    private readonly ScruffSettings _settings;
    private readonly BlueskyTarget _bluesky;
    private readonly MastodonTarget _mastodon;
    private readonly DiscordTarget _discord;
    private readonly DeviantArtTarget _deviantart;
    private readonly TumblrTarget _tumblr;
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
        _discord = new DiscordTarget(_http) { Webhook = LoadDiscord() };
        _deviantart = new DeviantArtTarget(_http) { Account = _settings.DeviantArtAccount };
        _tumblr = new TumblrTarget(_http)
        {
            Account = _settings.TumblrAccount,
            Blogs = _settings.TumblrBlogs,
            Blog = _settings.TumblrBlog,
        };
        LoadOAuth(_deviantart);
        LoadOAuth(_tumblr);
        _reddit = new RedditTarget { Subreddit = _settings.RedditSubreddit };

        IPostTarget[] all =
        [
            _bluesky, _mastodon, _discord, _deviantart, _tumblr,
            new FurAffinityTarget(), new XTarget(), new InstagramTarget(), _reddit,
        ];
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

    public IReadOnlyList<string> TumblrBlogs => _tumblr.Blogs;

    public string TumblrBlog
    {
        get => _tumblr.Blog;
        set
        {
            if (value is null || _tumblr.Blog == value)
                return;
            _tumblr.Blog = value;
            _settings.TumblrBlog = value;
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
        if (!Accepts(handoff))
            return;

        var before = Files.Count;
        AddPaths(handoff.Paths);
        var added = Files.Count - before;
        handoff.Answer(added == 1 ? _host.Text["scruff.reply.one"] : _host.Text.Format("scruff.reply.many", added));
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
                _host.Log(LogLevel.Warning, $"Scruff could not read {file.Path}: {ex.Message}");
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
        RebuildBeforePosting();
    }

    private void OnTargetsChanged()
    {
        _settings.EnabledTargets = Targets.Where(t => t.IsEnabled).Select(t => t.Id).ToList();
        Save();
        PostCommand.RaiseCanExecuteChanged();
        RebuildBeforePosting();
    }

    /// <summary>
    /// The strip above Post: every switched-on place's problems and notes in one list, one line
    /// per thing to say, with the places it applies to in front, so the same missing alt text on
    /// three places is one line and not three. Post stays as it was: most of these are notes, and
    /// the ones that are not are refused by their place at posting anyway.
    /// </summary>
    public ObservableCollection<BeforePostingLine> BeforePosting { get; } = [];

    /// <summary>Somewhere is switched on and nothing about the post is worth a word.</summary>
    public bool IsAllClear => Targets.Any(t => t.IsEnabled) && BeforePosting.Count == 0;

    public bool HasBeforePosting => BeforePosting.Count > 0;

    private void RebuildBeforePosting()
    {
        BeforePosting.Clear();
        var on = Targets.Where(t => t.IsEnabled).ToList();
        var lines = on.SelectMany(t => t.ProblemLines.Select(l => (t.Name, Line: l, Problem: true)))
            .Concat(on.SelectMany(t => t.NoteLines.Select(l => (t.Name, Line: l, Problem: false))));

        foreach (var group in lines.GroupBy(l => (l.Line, l.Problem)).OrderByDescending(g => g.Key.Problem))
        {
            var places = string.Join(", ", group.Select(g => g.Name).Distinct());
            BeforePosting.Add(new BeforePostingLine(places, group.Key.Line, group.Key.Problem));
        }

        OnPropertyChanged(nameof(IsAllClear));
        OnPropertyChanged(nameof(HasBeforePosting));
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
            _host.Log(LogLevel.Warning, $"Scruff cleaned {written} file(s), {removed} carried something, {failures.Count} failed.");
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

    /// <summary>
    /// A rule's "clean it": the one file the event was about, cleaned where it lies, whatever is
    /// in the tab's own pile. Only when there is something to take out; a picture that carries
    /// nothing is left exactly as it was, down to its date.
    /// </summary>
    public async Task<string> Perform(ActionRequest request, CancellationToken token)
    {
        if (request.Action != ScruffPlugin.CleanAction)
            throw new ActionDeclinedException(_host.Text.Format("scruff.action.unknown", request.Action));

        var path = request.Path;
        if (!File.Exists(path))
            throw new ActionDeclinedException(_host.Text.Format("scruff.action.gone", path));

        var name = Path.GetFileName(path);
        var text = _host.Text;
        return await Task.Run(() =>
        {
            var clean = Preparer.Clean(File.ReadAllBytes(path));
            if (clean.Format == ImageFormat.Unknown)
                throw new ActionDeclinedException(text.Format("scruff.error.notapicture", name));
            if (!clean.Original.CarriesAnything && !clean.Turned)
                return text.Format("scruff.action.nothing", name);

            token.ThrowIfCancellationRequested();
            var final = ReplaceInPlace(path, clean);
            _host.Log($"Scruff cleaned {path} for a rule{(final == path ? "" : $", now {Path.GetFileName(final)}")}.");
            return final == path
                ? text.Format("scruff.action.cleaned", name)
                : text.Format("scruff.action.renamed", name, Path.GetFileName(final));
        }, token);
    }

    /// <summary>
    /// A cleaned picture over its original: written beside it first, the original to the Recycle
    /// Bin, then into its place under the name its format calls for. The original's modified
    /// time is put back on it, because the bot orders a queue by that time and a file cleaned in
    /// a queue must not jump to the front of it. Throws with the reason when the original would
    /// not go to the bin, having taken the new copy away again.
    /// </summary>
    public static string ReplaceInPlace(string path, Prepared clean)
    {
        var folder = Path.GetDirectoryName(path) ?? "";
        var name = Path.GetFileName(path);
        var wanted = string.Equals(Path.GetExtension(name), clean.Extension, StringComparison.OrdinalIgnoreCase)
            ? name
            : Path.GetFileNameWithoutExtension(name) + clean.Extension;
        var final = Path.Combine(folder, wanted);
        var temp = Path.Combine(folder, "." + wanted + ".scruff");
        var written = File.GetLastWriteTimeUtc(path);

        File.WriteAllBytes(temp, clean.Bytes);
        var outcome = RecycleBin.Send([path]);
        if (outcome.Failed > 0)
        {
            File.Delete(temp);
            throw new IOException($"{name}: {outcome.FailureReason}");
        }

        File.Move(temp, final, overwrite: true);
        File.SetLastWriteTimeUtc(final, written);
        return final;
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
                            _host.Store.Record("posted", target.Name, result.Url ?? draft.Title, new Dictionary<string, string>
                            {
                                ["url"] = result.Url ?? "",
                                ["files"] = fitted.Count.ToString(),
                                ["title"] = draft.Title,
                            });
                        }
                        else
                        {
                            failed++;
                            target.Outcome = result.Error ?? _host.Text["scruff.outcome.failed"];
                            target.Failed = true;
                            _host.Log(LogLevel.Warning, $"Scruff could not post to {target.Name}: {result.Error}");
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
                    _host.Log(LogLevel.Warning, $"Scruff failed on {target.Name}: {ex}");
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

    private sealed class StoredDiscord
    {
        public string Url { get; set; } = "";

        public string Name { get; set; } = "";

        public string GuildId { get; set; } = "";

        public string ChannelId { get; set; } = "";
    }

    /// <summary>The app and the tokens together, since neither is any use without the other.</summary>
    private sealed class StoredOAuth
    {
        public string ClientId { get; set; } = "";

        public string ClientSecret { get; set; } = "";

        public string AccessToken { get; set; } = "";

        public string? RefreshToken { get; set; }

        public DateTimeOffset ExpiresAt { get; set; }
    }

    private DiscordWebhook? LoadDiscord()
    {
        var stored = Read<StoredDiscord>("discord");
        return stored is { Url.Length: > 0 }
            ? new DiscordWebhook(stored.Url, stored.Name, stored.GuildId, stored.ChannelId)
            : null;
    }

    /// <summary>
    /// Gives an OAuth place its app and tokens back, and the pen to write them again with,
    /// since a renewed token has to be sealed away the moment it arrives.
    /// </summary>
    private void LoadOAuth(OAuthTarget target)
    {
        target.Persist = (app, tokens) => _secrets.Set(target.Id, JsonSerializer.Serialize(new StoredOAuth
        {
            ClientId = app.ClientId,
            ClientSecret = app.ClientSecret,
            AccessToken = tokens.AccessToken,
            RefreshToken = tokens.RefreshToken,
            ExpiresAt = tokens.ExpiresAt,
        }));

        var stored = Read<StoredOAuth>(target.Id);
        if (stored is { ClientId.Length: > 0, ClientSecret.Length: > 0 })
        {
            target.App = new OAuthApp(stored.ClientId, stored.ClientSecret);
            target.Tokens = new OAuthTokens(stored.AccessToken, stored.RefreshToken, stored.ExpiresAt);
        }
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
            _host.Log(LogLevel.Warning, $"Scruff could not read the {name} credential: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Checks the login against the service and keeps it only if it works. The password or
    /// token is cleared from the box either way, so it is not left on screen.
    /// </summary>
    public async Task SignInAsync(TargetViewModel? card)
    {
        if (card is null || card.LoginA.Trim().Length == 0)
            return;
        if (!card.IsDiscord && card.LoginB.Trim().Length == 0)
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
            else if (card.Target is DiscordTarget)
            {
                var webhook = await _discord.VerifyAsync(card.LoginA, _closing.Token);

                _secrets.Set("discord", JsonSerializer.Serialize(new StoredDiscord
                {
                    Url = webhook.Url, Name = webhook.Name, GuildId = webhook.GuildId, ChannelId = webhook.ChannelId,
                }));
                _discord.Webhook = webhook;
                card.LoginA = "";
                card.Account = webhook.Name;
            }
            else if (card.Target is OAuthTarget oauth)
            {
                // The browser is about to open; say so, because the tab looks idle until it comes back.
                Status = _host.Text.Format("scruff.status.browser", card.Name);
                var app = new OAuthApp(card.LoginA.Trim(), card.LoginB.Trim());
                var account = await oauth.SignInAsync(app, url => Explorer.Open(url), _closing.Token);

                if (oauth is DeviantArtTarget)
                    _settings.DeviantArtAccount = account;
                else if (oauth is TumblrTarget tumblr)
                {
                    _settings.TumblrAccount = account;
                    _settings.TumblrBlogs = tumblr.Blogs.ToList();
                    _settings.TumblrBlog = tumblr.Blog;
                    OnPropertyChanged(nameof(TumblrBlogs));
                    OnPropertyChanged(nameof(TumblrBlog));
                }

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
            _host.Log(LogLevel.Warning, $"Scruff could not sign in to {card.Name}: {ex.Message}");
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
        else if (card.Target is DiscordTarget)
        {
            _secrets.Forget("discord");
            _discord.Webhook = null;
        }
        else if (card.Target is OAuthTarget oauth)
        {
            _secrets.Forget(oauth.Id);
            oauth.SignOut();
            if (oauth is DeviantArtTarget)
                _settings.DeviantArtAccount = null;
            else if (oauth is TumblrTarget tumblr)
            {
                _settings.TumblrAccount = null;
                _settings.TumblrBlogs = [];
                _settings.TumblrBlog = "";
                tumblr.Blogs = [];
                tumblr.Blog = "";
                OnPropertyChanged(nameof(TumblrBlogs));
                OnPropertyChanged(nameof(TumblrBlog));
            }
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
            _host.Log(LogLevel.Warning, $"Could not save Scruff settings: {ex.Message}");
        }
    }

    private bool _disposed;

    /// <summary>
    /// Twice, in practice: the shell disposes the view and then its DataContext, and the view
    /// disposes its DataContext itself. The second call has to be a no-op rather than a throw
    /// from a token source that is already gone.
    /// </summary>
    /// <summary>Ctrl+K reaching into the pile: a picture by name. Landing on one selects it.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        var hits = new List<SearchHit>();

        foreach (var file in Files)
        {
            if (hits.Count >= limit)
                break;
            if (!SearchWords.Match(words, file.Name))
                continue;

            var chosen = file;
            hits.Add(new SearchHit(file.Name, System.IO.Path.GetDirectoryName(file.Path) ?? "", () => Selected = chosen));
        }

        return hits;
    }

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

    // ---- picking, through the host's dialogs rather than a TopLevel of our own ----

    private RelayCommand? _pickOutputCommand;

    public RelayCommand PickOutputCommand => _pickOutputCommand ??= new RelayCommand(() => _ = PickOutputAsync());

    private async Task PickOutputAsync()
    {
        try
        {
            var picked = await _host.Pick.Folder(new PickOptions { Title = _host.Text["scruff.dialog.output"] });
            if (string.IsNullOrWhiteSpace(picked))
                return;
            SetOutputFolder(picked);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not pick: {ex.Message}");
        }
    }

    private RelayCommand? _addFilesCommand;

    public RelayCommand AddFilesCommand => _addFilesCommand ??= new RelayCommand(() => _ = PickFilesAsync());

    private async Task PickFilesAsync()
    {
        try
        {
            var picked = await _host.Pick.Files(new PickOptions
            {
                Title = _host.Text["scruff.dialog.files"],
                StartIn = LastFolder,
                Filters =
                [
                    PickFilter.Of(_host.Text["pick.pictures"], "*.jpg", "*.jpeg", "*.png", "*.gif", "*.webp", "*.bmp"),
                    PickFilter.Of(_host.Text["pick.all"], "*"),
                ],
            });
            if (picked.Count == 0)
                return;
            AddPaths(picked);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not pick: {ex.Message}");
        }
    }

    private RelayCommand? _addFolderCommand;

    public RelayCommand AddFolderCommand => _addFolderCommand ??= new RelayCommand(() => _ = PickFolderAsync());

    private async Task PickFolderAsync()
    {
        try
        {
            var picked = await _host.Pick.Folder(new PickOptions { Title = _host.Text["scruff.dialog.folder"] });
            if (string.IsNullOrWhiteSpace(picked))
                return;
            AddPaths([picked]);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not pick: {ex.Message}");
        }
    }
}
