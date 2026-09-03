using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Saucer.Services;

namespace Meows.Plugins.Saucer.ViewModels;

public sealed class SaucerSettings
{
    /// <summary>Where saved images land. Point Kibble at the same folder and the two meet.</summary>
    public string? IntakeFolder { get; set; }

    /// <summary>Save every image the moment it is copied, without being asked.</summary>
    public bool AutoSaveImages { get; set; }

    public bool WatchClipboard { get; set; } = true;
}

public sealed class ClipViewModel : ObservableObject, IDisposable
{
    private Bitmap? _thumbnail;
    private bool _isPinned;
    private string? _savedTo;

    public ClipViewModel(Clipping clipping, DateTime taken)
    {
        Clipping = clipping;
        Taken = taken;

        if (clipping.IsImage)
            Thumbnail = Decode(clipping.Image!);
    }

    public Clipping Clipping { get; }

    public DateTime Taken { get; }

    public bool IsImage => Clipping.IsImage;

    public string TimeText => Taken.ToString("HH:mm:ss");

    public string Summary => IsImage
        ? Thumbnail is { } picture
            ? MeowsText.Current.Format("saucer.image.size", picture.PixelSize.Width, picture.PixelSize.Height)
            : MeowsText.Current["saucer.image"]
        : WindowsClipboard.Summarise(Clipping.Text ?? "");

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            var old = _thumbnail;
            if (!SetField(ref _thumbnail, value))
                return;
            OnPropertyChanged(nameof(HasThumbnail));
            old?.Dispose();
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    /// <summary>Pinned clippings are never pushed out by newer ones.</summary>
    public bool IsPinned
    {
        get => _isPinned;
        set => SetField(ref _isPinned, value);
    }

    public string? SavedTo
    {
        get => _savedTo;
        set
        {
            if (SetField(ref _savedTo, value))
                OnPropertyChanged(nameof(IsSaved));
        }
    }

    public bool IsSaved => !string.IsNullOrEmpty(SavedTo);

    private static Bitmap? Decode(byte[] bytes)
    {
        try
        {
            using var stream = new MemoryStream(bytes);
            return new Bitmap(stream);
        }
        catch (Exception)
        {
            // Something on the clipboard claiming to be an image and not being one is not
            // worth making a fuss about.
            return null;
        }
    }

    public void Dispose() => Thumbnail = null;
}

public sealed class SaucerViewModel : ObservableObject, IDisposable
{
    /// <summary>How many clippings to keep. Enough to scroll back through, not enough to hoard.</summary>
    private const int Keep = 40;

    private readonly IMeowsHost _host;
    private SaucerSettings _settings;
    private IBackgroundTask? _watch;
    private uint _lastSeen;

    private ClipViewModel? _selected;
    private string _status = MeowsText.Current["saucer.status.watching"];
    private string? _errorMessage;

    public SaucerViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<SaucerSettings>() ?? new SaucerSettings();
        _settings.IntakeFolder ??= DefaultIntake();

        SaveCommand = new RelayCommand(() => Save(Selected), () => Selected is { IsImage: true });
        CopyAgainCommand = new RelayCommand(p => CopyBack(p as ClipViewModel));
        PinCommand = new RelayCommand(p => Pin(p as ClipViewModel));
        ForgetCommand = new RelayCommand(p => Forget(p as ClipViewModel));
        ClearCommand = new RelayCommand(Clear);
        OpenIntakeCommand = new RelayCommand(() => Open(IntakeFolder), () => Directory.Exists(IntakeFolder));

        StartWatching();
    }

    public ObservableCollection<ClipViewModel> Clips { get; } = new();

    public RelayCommand SaveCommand { get; }

    public RelayCommand CopyAgainCommand { get; }

    public RelayCommand PinCommand { get; }

    public RelayCommand ForgetCommand { get; }

    public RelayCommand ClearCommand { get; }

    public RelayCommand OpenIntakeCommand { get; }

    public string IntakeFolder => _settings.IntakeFolder ?? "";

    public bool AutoSaveImages
    {
        get => _settings.AutoSaveImages;
        set
        {
            if (_settings.AutoSaveImages == value)
                return;
            _settings.AutoSaveImages = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public bool WatchClipboard
    {
        get => _settings.WatchClipboard;
        set
        {
            if (_settings.WatchClipboard == value)
                return;
            _settings.WatchClipboard = value;
            SaveSettings();
            OnPropertyChanged();
            Status = _host.Text[value ? "saucer.status.watching" : "saucer.status.off"];
        }
    }

    public ClipViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value))
                return;
            OnPropertyChanged(nameof(HasSelection));
            SaveCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelection => Selected is not null;

    public bool IsEmpty => Clips.Count == 0;

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

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public void SetIntakeFolder(string folder)
    {
        _settings.IntakeFolder = folder;
        SaveSettings();
        OnPropertyChanged(nameof(IntakeFolder));
        OpenIntakeCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Polls the clipboard's sequence number, which changes whenever anything is copied and
    /// costs nothing to read. Cheaper and far simpler than owning a hidden window to be told.
    /// </summary>
    private void StartWatching()
    {
        _lastSeen = WindowsClipboard.SequenceNumber();

        _watch = _host.Background.Schedule(_host.Text["saucer.task.watch"], TimeSpan.FromMilliseconds(600),
            async context =>
            {
                if (!WatchClipboard)
                    return;

                var now = WindowsClipboard.SequenceNumber();
                if (now == _lastSeen)
                    return;

                _lastSeen = now;
                var clipping = WindowsClipboard.Read();
                if (clipping.Kind == ClippingKind.Nothing)
                    return;

                await Dispatcher.UIThread.InvokeAsync(() => Add(clipping));
            });
    }

    /// <summary>Takes a new clipping. Public so it can be exercised without a real clipboard.</summary>
    public void Add(Clipping clipping)
    {
        // The same thing copied twice running is not two clippings.
        if (Clips.FirstOrDefault() is { } newest && Same(newest.Clipping, clipping))
            return;

        var clip = new ClipViewModel(clipping, DateTime.Now);
        Clips.Insert(0, clip);
        Trim();

        Selected ??= clip;
        OnPropertyChanged(nameof(IsEmpty));

        if (clip.IsImage && AutoSaveImages)
            Save(clip);
    }

    private static bool Same(Clipping a, Clipping b)
    {
        if (a.Kind != b.Kind)
            return false;

        return a.Kind == ClippingKind.Text
            ? a.Text == b.Text
            : a.Image is not null && b.Image is not null && a.Image.AsSpan().SequenceEqual(b.Image);
    }

    private void Trim()
    {
        while (Clips.Count > Keep)
        {
            var oldest = Clips.LastOrDefault(c => !c.IsPinned);
            if (oldest is null)
                return;

            Clips.Remove(oldest);
            if (ReferenceEquals(Selected, oldest))
                Selected = Clips.FirstOrDefault();
            oldest.Dispose();
        }
    }

    /// <summary>
    /// Writes an image into the intake folder as a PNG. Always PNG, because a clipboard bitmap
    /// is uncompressed and a screenshot sized one runs to megabytes for no reason.
    /// </summary>
    public string? Save(ClipViewModel? clip)
    {
        if (clip is not { IsImage: true } || clip.Thumbnail is null)
            return null;

        try
        {
            Directory.CreateDirectory(IntakeFolder);
            var path = Unique(Path.Combine(IntakeFolder, WindowsClipboard.SuggestName(clip.Taken, ".png")));

            clip.Thumbnail.Save(path, new PngBitmapEncoderOptions());
            clip.SavedTo = path;

            Status = _host.Text.Format("saucer.status.saved", Path.GetFileName(path));
            _host.Log($"Saucer saved a clipping to {path}");
            return path;
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("saucer.error.save", IntakeFolder, ex.Message);
            _host.Log($"Saucer could not save a clipping: {ex.Message}");
            return null;
        }
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path))
            return path;

        var folder = Path.GetDirectoryName(path)!;
        var stem = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);

        for (var n = 2; n < 1000; n++)
        {
            var candidate = Path.Combine(folder, $"{stem}_{n}{extension}");
            if (!File.Exists(candidate))
                return candidate;
        }

        return Path.Combine(folder, $"{stem}_{Guid.NewGuid():N}{extension}");
    }

    private void CopyBack(ClipViewModel? clip)
    {
        if (clip is not { IsImage: false } || clip.Clipping.Text is not { } text)
            return;

        // Text only. Putting an image back on the clipboard is a different and much fiddlier
        // job, and the useful thing to do with an image here is save it.
        //
        // Writing it will bump the sequence number and come straight back round as a new
        // clipping, which Add throws away as a repeat of what is already at the top.
        Status = _host.Text[WindowsClipboard.SetText(text) ? "saucer.status.copied" : "saucer.status.copyfailed"];
    }

    private void Pin(ClipViewModel? clip)
    {
        if (clip is null)
            return;

        clip.IsPinned = !clip.IsPinned;
    }

    private void Forget(ClipViewModel? clip)
    {
        if (clip is null)
            return;

        Clips.Remove(clip);
        if (ReferenceEquals(Selected, clip))
            Selected = Clips.FirstOrDefault();

        clip.Dispose();
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void Clear()
    {
        foreach (var clip in Clips.ToList())
            clip.Dispose();

        Clips.Clear();
        Selected = null;
        OnPropertyChanged(nameof(IsEmpty));
        Status = _host.Text["saucer.status.cleared"];
    }

    private static string DefaultIntake()
    {
        var pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
        return Path.Combine(pictures, "Kibble intake");
    }

    private void Open(string? path)
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
            ErrorMessage = _host.Text.Format("saucer.error.open", path, ex.Message);
        }
    }

    private void SaveSettings()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log($"Could not save Saucer settings: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _watch?.Cancel();
        foreach (var clip in Clips)
            clip.Dispose();
    }
}
