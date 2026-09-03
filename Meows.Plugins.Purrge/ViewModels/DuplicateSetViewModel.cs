using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Purrge.Services;

namespace Meows.Plugins.Purrge.ViewModels;

/// <summary>Which timestamp we mean by oldest and newest. Copying treats them differently.</summary>
public enum AgeBasis
{
    Modified,
    Created,
}

/// <summary>
/// One row of the "compare ages by" dropdown. Label is the shared bindable string for its key, so
/// the dropdown reads correctly the moment the language changes rather than on the next restart.
/// </summary>
public sealed class AgeBasisOption(AgeBasis value, string key)
{
    public AgeBasis Value { get; } = value;

    public TranslatedString Label { get; } = MeowsText.Entry(key);
}

public sealed class DuplicateFileViewModel : ObservableObject, IDisposable
{
    private Bitmap? _thumbnail;
    private bool _attempted;
    private bool _isSelected;

    public DuplicateFileViewModel(DuplicateFile file)
    {
        File = file;
        FileName = Path.GetFileName(file.Path);
        Folder = Path.GetDirectoryName(file.Path) ?? "";
    }

    public DuplicateFile File { get; }

    public string FullPath => File.Path;

    public string FileName { get; }

    public string Folder { get; }

    public string ModifiedText => File.ModifiedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public string CreatedText => File.CreatedUtc.ToLocalTime().ToString("yyyy-MM-dd HH:mm");

    public bool IsImage => PreviewSupport.IsRenderable(File.Path);

    /// <summary>The row the preview and the delete button refer to.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetField(ref _isSelected, value);
    }

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

    public async Task LoadThumbnailAsync(int width, CancellationToken token)
    {
        if (_attempted || !IsImage)
            return;
        _attempted = true;

        var bitmap = await Task.Run(() => PreviewSupport.Decode(File.Path, width), token).ConfigureAwait(true);
        if (token.IsCancellationRequested)
        {
            bitmap?.Dispose();
            return;
        }

        Thumbnail = bitmap;
    }

    public void Dispose()
    {
        _thumbnail?.Dispose();
        _thumbnail = null;
    }
}

public sealed class DuplicateSetViewModel : ObservableObject, IDisposable
{
    public DuplicateSetViewModel(DuplicateSet set)
    {
        Size = set.Size;
        RedundantBytes = set.RedundantBytes;
        Files = new ObservableCollection<DuplicateFileViewModel>(
            set.Files.Select(f => new DuplicateFileViewModel(f)));
    }

    public ObservableCollection<DuplicateFileViewModel> Files { get; }

    public long Size { get; }

    public long RedundantBytes { get; private set; }

    public int Count => Files.Count;

    public string Header => $"{Files.Count} copies · {Format(Size)} each";

    public string SubHeader => $"{Format(RedundantBytes)} recoverable";

    /// <summary>The one that survives Keep oldest, going by the chosen timestamp.</summary>
    public DuplicateFileViewModel? Oldest(AgeBasis basis) =>
        Files.OrderBy(f => Stamp(f, basis)).FirstOrDefault();

    public DuplicateFileViewModel? Newest(AgeBasis basis) =>
        Files.OrderByDescending(f => Stamp(f, basis)).FirstOrDefault();

    private static DateTime Stamp(DuplicateFileViewModel file, AgeBasis basis) =>
        basis == AgeBasis.Created ? file.File.CreatedUtc : file.File.ModifiedUtc;

    /// <summary>Takes out the files that went, and redoes the totals.</summary>
    public void Remove(IEnumerable<DuplicateFileViewModel> removed)
    {
        foreach (var file in removed.ToList())
        {
            Files.Remove(file);
            file.Dispose();
        }

        RedundantBytes = Size * Math.Max(0, Files.Count - 1);
        OnPropertyChanged(nameof(Count));
        OnPropertyChanged(nameof(Header));
        OnPropertyChanged(nameof(SubHeader));
        OnPropertyChanged(nameof(RedundantBytes));
    }

    public static string Format(long bytes) => bytes switch
    {
        >= 1024L * 1024 * 1024 => $"{bytes / 1024d / 1024 / 1024:0.##} GB",
        >= 1024 * 1024 => $"{bytes / 1024d / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B",
    };

    public void Dispose()
    {
        foreach (var file in Files)
            file.Dispose();
    }
}

internal static class PreviewSupport
{
    private static readonly string[] Renderable =
        [".jpg", ".jpeg", ".png", ".webp", ".jfif", ".bmp", ".gif"];

    public static bool IsRenderable(string path) =>
        Renderable.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase);

    public static Bitmap? Decode(string path, int width)
    {
        try
        {
            // Shared, so previewing a file cannot stop it being sent to the Recycle Bin.
            using var stream = new System.IO.FileStream(path, System.IO.FileMode.Open,
                System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite | System.IO.FileShare.Delete);
            return Bitmap.DecodeToWidth(stream, width);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
