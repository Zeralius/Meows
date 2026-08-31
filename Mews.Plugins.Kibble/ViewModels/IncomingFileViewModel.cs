using Avalonia.Media.Imaging;
using Mews.Bot;
using Mews.Plugins.Abstractions;

namespace Mews.Plugins.Kibble.ViewModels;

/// <summary>One file waiting in the folder you opened.</summary>
public sealed class IncomingFileViewModel : ObservableObject, IDisposable
{
    private Bitmap? _thumbnail;
    private bool _attempted;
    private int _pageNumber;

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

    /// <summary>
    /// For files the folder scan already measured. A directory walk hands back the size and
    /// the timestamp with it, so asking the filesystem a second time per file is pure waste,
    /// and on a few thousand files that waste is most of the wait.
    /// </summary>
    public IncomingFileViewModel(string path, long size, DateTime modified)
    {
        Path = path;
        FileName = System.IO.Path.GetFileName(path);
        Kind = MediaRules.KindOf(path);
        SizeBytes = size;
        Modified = modified;
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

    /// <summary>
    /// Which page this file will be inside the comic, or 0 when it is not part of one. Shown on
    /// the tile so the page order is visible before the archive exists rather than after.
    /// </summary>
    public int PageNumber
    {
        get => _pageNumber;
        set
        {
            if (!SetField(ref _pageNumber, value))
                return;
            OnPropertyChanged(nameof(PageText));
            OnPropertyChanged(nameof(HasPageNumber));
        }
    }

    public string PageText => _pageNumber > 0 ? _pageNumber.ToString() : "";

    public bool HasPageNumber => _pageNumber > 0;

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

    /// <summary>
    /// Whether a decode has been run for this tile. False after a cancelled one, so a later
    /// pass tries again. Public because "did every visible tile get asked?" is the invariant
    /// worth pinning down, and a blank tile is otherwise indistinguishable from a decode that
    /// legitimately produced nothing.
    /// </summary>
    public bool ThumbnailAttempted => _attempted;

    /// <summary>
    /// Decodes once. A file that has no thumbnail to give, a video or a pdf, is not asked
    /// twice, but being cancelled does not count as having tried: the flag goes back so a
    /// later pass picks the tile up, instead of leaving it blank for the rest of the session.
    /// </summary>
    public async Task LoadThumbnailAsync(int width, CancellationToken token)
    {
        if (_attempted)
            return;
        _attempted = true;

        Bitmap? bitmap;
        try
        {
            bitmap = await Task.Run(() => Decode(width), token).ConfigureAwait(true);
        }
        catch (OperationCanceledException)
        {
            _attempted = false;
            throw;
        }

        if (token.IsCancellationRequested)
        {
            bitmap?.Dispose();
            _attempted = false;
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

            using var stream = MediaRules.OpenShared(Path);
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
