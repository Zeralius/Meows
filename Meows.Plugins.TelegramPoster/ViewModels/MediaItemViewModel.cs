using Avalonia.Media.Imaging;
using Meows.Plugins.Abstractions;
using Meows.Plugins.TelegramPoster.Services;
using Meows.Bot;

namespace Meows.Plugins.TelegramPoster.ViewModels;

/// <summary>One file in the queue or the archive.</summary>
public sealed class MediaItemViewModel : ObservableObject, IDisposable
{
    private Bitmap? _thumbnail;
    private bool _thumbnailAttempted;

    public MediaItemViewModel(string path)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        Kind = MediaRules.KindOf(path);

        try
        {
            var info = new FileInfo(path);
            SizeBytes = info.Length;
            Modified = info.LastWriteTime;
        }
        catch (Exception)
        {
            SizeBytes = 0;
            Modified = DateTime.MinValue;
        }
    }

    public string Path { get; }

    public string FileName { get; }

    public MediaKind Kind { get; }

    public long SizeBytes { get; }

    public DateTime Modified { get; }

    public bool IsComic => Kind == MediaKind.Comic;

    public string SizeText => SizeBytes switch
    {
        >= 1024 * 1024 => $"{SizeBytes / 1024d / 1024d:0.#} MB",
        >= 1024 => $"{SizeBytes / 1024d:0} KB",
        _ => $"{SizeBytes} B",
    };

    public string ModifiedText => Modified == DateTime.MinValue ? "?" : Modified.ToString("yyyy-MM-dd HH:mm");

    /// <summary>Stand-in for anything we cannot draw.</summary>
    public string KindGlyph => Kind switch
    {
        MediaKind.Video => "▶",
        MediaKind.Document => "📄",
        MediaKind.Comic => "📚",
        MediaKind.Animation => "GIF",
        _ => "?",
    };

    public string KindText => Kind switch
    {
        MediaKind.Photo => "Photo",
        MediaKind.Video => "Video",
        MediaKind.Animation => "Animation",
        MediaKind.Document => "Document",
        MediaKind.Comic => "Comic archive",
        _ => "Unsupported",
    };

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            var old = _thumbnail;
            if (SetField(ref _thumbnail, value))
            {
                OnPropertyChanged(nameof(HasThumbnail));
                OnPropertyChanged(nameof(ShowGlyph));
                old?.Dispose();
            }
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public bool ShowGlyph => _thumbnail is null;

    /// <summary>Decodes off the UI thread. Comics show their first page.</summary>
    public async Task LoadThumbnailAsync(int width, CancellationToken token)
    {
        if (_thumbnailAttempted)
            return;
        _thumbnailAttempted = true;

        var bitmap = await Task.Run(() => Decode(width), token).ConfigureAwait(true);
        if (token.IsCancellationRequested)
        {
            bitmap?.Dispose();
            return;
        }

        Thumbnail = bitmap;
    }

    private Bitmap? Decode(int width)
    {
        try
        {
            if (IsComic)
            {
                var cover = MediaRules.ComicCover(Path);
                if (cover is null)
                    return null;
                using var coverStream = new MemoryStream(cover);
                return Bitmap.DecodeToWidth(coverStream, width);
            }

            if (!MediaRules.IsRenderableImage(Path))
                return null;

            using var stream = MediaRules.OpenShared(Path);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception)
        {
            // Avalonia cannot read every codec. That is a display problem, not a posting one.
            return null;
        }
    }

    public void Dispose()
    {
        _thumbnail?.Dispose();
        _thumbnail = null;
    }
}
