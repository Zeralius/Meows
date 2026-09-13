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
    private Heavy? _trouble;
    private bool _weighed;

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
        MediaKind.Photo => MeowsText.Current["tp.kind.photo"],
        MediaKind.Video => MeowsText.Current["tp.kind.video"],
        MediaKind.Animation => MeowsText.Current["tp.kind.animation"],
        MediaKind.Document => MeowsText.Current["tp.kind.document"],
        MediaKind.Comic => MeowsText.Current["tp.kind.comic"],
        _ => MeowsText.Current["tp.kind.unsupported"],
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

    // ---- whether the bot would take it ----

    /// <summary>Portion's verdict on this file, or null when the bot would take it or it has not been weighed.</summary>
    public Heavy? Trouble
    {
        get => _trouble;
        private set
        {
            if (!SetField(ref _trouble, value))
                return;
            OnPropertyChanged(nameof(WillFail));
            OnPropertyChanged(nameof(HasNote));
            OnPropertyChanged(nameof(CanShrink));
            OnPropertyChanged(nameof(TroubleText));
        }
    }

    /// <summary>The post would fail the moment it fired. The badge on the tile.</summary>
    public bool WillFail => _trouble is { WillFail: true };

    /// <summary>The bot would do something surprising rather than fail: many batches, skipped files.</summary>
    public bool HasNote => _trouble is { WillFail: false };

    public bool CanShrink => _trouble is { CanShrink: true };

    public string TroubleText => _trouble is null ? "" : Portion(_trouble);

    private static string Portion(Heavy heavy)
    {
        var text = MeowsText.Current;
        var first = heavy.Troubles.FirstOrDefault();
        var line = first switch
        {
            Meows.Bot.Trouble.OverBytes => text.Format("tp.trouble.overbytes", heavy.Limit / 1_000_000),
            Meows.Bot.Trouble.TooManyPixels => text["tp.trouble.pixels"],
            Meows.Bot.Trouble.OddRatio => text["tp.trouble.ratio"],
            Meows.Bot.Trouble.HeavyPages => text.Format("tp.trouble.heavypages", heavy.HeavyPageCount),
            Meows.Bot.Trouble.EmptyComic => text["tp.trouble.emptycomic"],
            Meows.Bot.Trouble.BadPages => text.Format("tp.trouble.badpages", heavy.BadPageCount),
            Meows.Bot.Trouble.ForeignFiles => text.Format("tp.trouble.foreign", heavy.ForeignCount),
            Meows.Bot.Trouble.ManyBatches => text.Format("tp.trouble.batches", heavy.Batches),
            _ => "",
        };
        return heavy.CanShrink ? $"{line} {text["tp.trouble.shrinkable"]}" : line;
    }

    /// <summary>
    /// Weighs the file the way Portion does, off the UI thread, once. Only a queue item is
    /// weighed: the archive has already gone out, and nothing about it is worth a badge.
    /// </summary>
    public async Task WeighAsync(GroupConfig group, CancellationToken token)
    {
        if (_weighed)
            return;
        _weighed = true;

        var heavy = await Task.Run(() => Weigher.Inspect(group, Path), token).ConfigureAwait(true);
        if (!token.IsCancellationRequested)
            Trouble = heavy;
    }

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

    private Bitmap? Decode(int width) => MediaRules.Thumbnail(Path, width);

    public void Dispose()
    {
        _thumbnail?.Dispose();
        _thumbnail = null;
    }
}
