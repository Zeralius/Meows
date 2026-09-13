using Avalonia.Media.Imaging;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Scruff.Services;

namespace Meows.Plugins.Scruff.ViewModels;

/// <summary>
/// One picture on the pile: where it is, what it carries, and what it becomes once cleaned.
///
/// The cleaning is done once and kept. Fitting to a particular place is done from the clean
/// copy each time, because the places want different things and none of them should get a
/// picture that was shrunk for another.
/// </summary>
public sealed class FileViewModel : ObservableObject, IDisposable
{
    private Bitmap? _thumbnail;
    private string _alt = "";
    private Prepared? _clean;
    private MetadataReport? _report;
    private string? _failure;

    public FileViewModel(string path)
    {
        Path = path;
        Name = System.IO.Path.GetFileName(path);
        try
        {
            Size = new FileInfo(path).Length;
        }
        catch (Exception)
        {
            Size = 0;
        }
    }

    public string Path { get; }

    public string Name { get; }

    public long Size { get; }

    /// <summary>The name a cleaned copy gets: the same, unless the format had to change.</summary>
    public string CleanName => _clean is null || _clean.Format == ImageFormat.Unknown ||
                               string.Equals(System.IO.Path.GetExtension(Name), _clean.Extension, StringComparison.OrdinalIgnoreCase)
        ? Name
        : System.IO.Path.GetFileNameWithoutExtension(Name) + _clean.Extension;

    /// <summary>What was found before anything was done.</summary>
    public MetadataReport? Report
    {
        get => _report;
        set
        {
            if (!SetField(ref _report, value))
                return;
            OnPropertyChanged(nameof(HasGps));
            OnPropertyChanged(nameof(CarriesAnything));
            OnPropertyChanged(nameof(NeedsTurning));
            OnPropertyChanged(nameof(IsUnknown));
            OnPropertyChanged(nameof(CarriedText));
            OnPropertyChanged(nameof(Summary));
        }
    }

    /// <summary>The cleaned picture, once it has been made.</summary>
    public Prepared? Clean
    {
        get => _clean;
        set
        {
            if (!SetField(ref _clean, value))
                return;
            OnPropertyChanged(nameof(IsCleaned));
            OnPropertyChanged(nameof(CleanName));
            OnPropertyChanged(nameof(Summary));
        }
    }

    public bool IsCleaned => _clean is not null;

    /// <summary>Why it could not be read, when it could not.</summary>
    public string? Failure
    {
        get => _failure;
        set
        {
            if (SetField(ref _failure, value))
                OnPropertyChanged(nameof(HasFailure));
        }
    }

    public bool HasFailure => _failure is not null;

    public bool HasGps => _report?.HasGps ?? false;

    public bool CarriesAnything => _report?.CarriesAnything ?? false;

    public bool NeedsTurning => _report?.NeedsTurning ?? false;

    public bool IsUnknown => _report is { Format: ImageFormat.Unknown };

    public string CarriedText => _report is null ? "" : string.Join(", ", _report.Carried);

    /// <summary>Size, kind and dimensions on one line, for under the thumbnail.</summary>
    public string Summary
    {
        get
        {
            var parts = new List<string>();
            var report = _report;
            if (report is { HasSize: true })
                parts.Add($"{report.Width}×{report.Height}");
            if (report is not null && report.Format != ImageFormat.Unknown)
                parts.Add(report.Format.ToString().ToUpperInvariant());
            parts.Add(Checks.Humanise(Size));
            return string.Join(" · ", parts);
        }
    }

    /// <summary>The description that travels with the picture wherever descriptions are taken.</summary>
    public string Alt
    {
        get => _alt;
        set => SetField(ref _alt, value ?? "");
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

    public void Dispose() => Thumbnail = null;
}
