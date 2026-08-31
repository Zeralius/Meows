using Avalonia.Media.Imaging;
using Mews.Bot;
using Mews.Plugins.Abstractions;

namespace Mews.Plugins.Kibble.ViewModels;

/// <summary>One file waiting in the folder you opened.</summary>
public sealed class IncomingFileViewModel : ObservableObject, IDisposable
{
    private Bitmap? _thumbnail;
    private bool _attempted;

    public IncomingFileViewModel(string path)
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

    public bool IsPostable => Kind != MediaKind.Unsupported;

    public string SizeText => SizeBytes switch
    {
        >= 1024 * 1024 => $"{SizeBytes / 1024d / 1024d:0.#} MB",
        >= 1024 => $"{SizeBytes / 1024d:0} KB",
        _ => $"{SizeBytes} B",
    };

    public string ModifiedText => Modified == DateTime.MinValue ? "?" : Modified.ToString("yyyy-MM-dd HH:mm");

    public string KindGlyph => Kind switch
    {
        MediaKind.Video => "▶",
        MediaKind.Document => "📄",
        MediaKind.Comic => "📚",
        MediaKind.Animation => "GIF",
        MediaKind.Photo => "🖼",
        _ => "?",
    };

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            var old = _thumbnail;
            if (!SetField(ref _thumbnail, value))
                return;
            OnPropertyChanged(nameof(HasThumbnail));
            OnPropertyChanged(nameof(ShowGlyph));
            old?.Dispose();
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public bool ShowGlyph => _thumbnail is null;

    public async Task LoadThumbnailAsync(int width, CancellationToken token)
    {
        if (_attempted)
            return;
        _attempted = true;

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
            if (MediaRules.IsComic(Path))
            {
                var cover = MediaRules.ComicCover(Path);
                if (cover is null)
                    return null;
                using var coverStream = new MemoryStream(cover);
                return Bitmap.DecodeToWidth(coverStream, width);
            }

            if (!MediaRules.IsRenderableImage(Path))
                return null;

            using var stream = File.OpenRead(Path);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Dispose()
    {
        _thumbnail?.Dispose();
        _thumbnail = null;
    }
}
