using System.Collections.ObjectModel;
using Avalonia.Threading;
using Meows.Disk;
using Meows.Plugins.Abstractions;

namespace Meows.Plugins.Scoop.ViewModels;

public sealed class ScoopSettings
{
    /// <summary>The drive whose bin was open last, so the tab reopens where it was left.</summary>
    public string? Drive { get; set; }
}

/// <summary>One drive in the left-hand list: its bin, in a line.</summary>
public sealed class DriveViewModel(BinDrive drive)
{
    public BinDrive Drive { get; } = drive;

    public string Root => Drive.Root;

    public string SizeText => RecycleBinContents.Humanise(Drive.Bytes);

    public string CountText => Drive.Count == 1
        ? MeowsText.Current["scoop.count.one"]
        : MeowsText.Current.Format("scoop.count.many", Drive.Count);

    public bool IsEmpty => Drive.Count == 0 && Drive.Unreadable is null;

    public bool IsUnreadable => Drive.Unreadable is not null;

    public string? Unreadable => Drive.Unreadable;
}

/// <summary>One thing in the bin: where it came from, what it cost, and when it went.</summary>
public sealed class BinItemViewModel(BinItem item, DateTime now)
{
    public BinItem Item { get; } = item;

    public string Name => Item.Name;

    public string OriginalFolder => Item.OriginalFolder;

    public string SizeText => RecycleBinContents.Humanise(Item.Size);

    public long Size => Item.Size;

    public DateTime DeletedAt => Item.DeletedAt;

    public string Glyph => Item.IsFolder ? "📁" : "📄";

    /// <summary>
    /// "14 months ago", in the words the rest of the app uses for the same idea. A time of
    /// MinValue is what a metadata file with a stamp that is not a FILETIME gets, and saying
    /// "56000 days ago" would be worse than admitting it is not known.
    /// </summary>
    public string WhenText
    {
        get
        {
            var text = MeowsText.Current;
            if (Item.DeletedAt == DateTime.MinValue)
                return text["scoop.when.unknown"];
            var days = Item.Days(now);
            return days switch
            {
                0 => text["scoop.when.today"],
                1 => text["scoop.when.yesterday"],
                < 30 => text.Format("scoop.when.days", days),
                < 365 => text.Format("scoop.when.months", days / 30),
                _ => text.Format("scoop.when.years", days / 365),
            };
        }
    }

    /// <summary>Something is at the original path now, so putting this back would land on it.</summary>
    public bool IsBlocked => Item.IsBlocked;
}

/// <summary>
/// The Recycle Bin, read rather than deleted into. Every other plugin here ends its rows at the
/// bin and none of them can see it, and the walker skips <c>$Recycle.Bin</c> on every scan, so
/// Chonk answers where the room went while stepping over the one folder holding everything
/// already decided against.
///
/// Read-only apart from two verbs, and the two are deliberately different weights: a restore is
/// one row and reversible by deleting it again, and emptying a drive is the only thing in Meows
/// that cannot be undone at all.
/// </summary>
public sealed class ScoopViewModel : ObservableObject, IDisposable, ISearchable, IGlanceable
{
    private readonly IMeowsHost _host;
    private readonly ScoopSettings _settings;
    private readonly LanguageWatch _language;

    private IReadOnlyList<BinDrive> _read = [];
    private DriveViewModel? _selectedDrive;
    private BinItemViewModel? _selectedItem;
    private string? _status;
    private string? _errorMessage;
    private string? _confirmingEmptyOf;
    private bool _isReading;
    private bool _hasRead;

    public ScoopViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<ScoopSettings>() ?? new ScoopSettings();
        _language = new LanguageWatch(OnEverythingChanged);

        RefreshCommand = new RelayCommand(Refresh, () => !_isReading);
        RestoreCommand = new RelayCommand(Restore, () => SelectedItem is { IsBlocked: false } && !_isReading);
        EmptyCommand = new RelayCommand(Empty, () => SelectedDrive is { Drive.Count: > 0 } && !_isReading);
        ShowOriginCommand = new RelayCommand(ShowOrigin, () => SelectedItem is not null);

        Refresh();
    }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand RestoreCommand { get; }

    public RelayCommand EmptyCommand { get; }

    public RelayCommand ShowOriginCommand { get; }

    public ObservableCollection<DriveViewModel> Drives { get; } = [];

    public ObservableCollection<BinItemViewModel> Items { get; } = [];

    public bool HasDrives => Drives.Count > 0;

    public bool IsEmpty => _hasRead && !_isReading && Items.Count == 0;

    public bool IsReading => _isReading;

    public DriveViewModel? SelectedDrive
    {
        get => _selectedDrive;
        set
        {
            if (!SetField(ref _selectedDrive, value))
                return;
            _settings.Drive = value?.Root;
            SaveSettings();
            CancelEmpty();
            ShowItems();
            EmptyCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(DriveUnreadable));
            OnPropertyChanged(nameof(HasDriveTrouble));
        }
    }

    public BinItemViewModel? SelectedItem
    {
        get => _selectedItem;
        set
        {
            if (!SetField(ref _selectedItem, value))
                return;
            RestoreCommand.RaiseCanExecuteChanged();
            ShowOriginCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(BlockedText));
            OnPropertyChanged(nameof(IsSelectionBlocked));
        }
    }

    public string? DriveUnreadable => SelectedDrive?.Unreadable;

    public bool HasDriveTrouble => DriveUnreadable is not null;

    public bool IsSelectionBlocked => SelectedItem is { IsBlocked: true };

    /// <summary>Why Restore is greyed: something is standing where this came from.</summary>
    public string BlockedText => SelectedItem is { IsBlocked: true } item
        ? _host.Text.Format("disk.bin.inthewaynow", item.Item.OriginalPath)
        : "";

    /// <summary>The headline: everything, across every drive, in one line.</summary>
    public string SummaryText
    {
        get
        {
            var text = _host.Text;
            if (!_hasRead)
                return text["scoop.status.ready"];

            var bytes = _read.Sum(d => d.Bytes);
            var count = _read.Sum(d => d.Count);
            if (count == 0)
                return text["scoop.summary.nothing"];

            var drives = _read.Count(d => d.Count > 0);
            var line = text.Format("scoop.summary", RecycleBinContents.Humanise(bytes), count, drives);

            if (Oldest is { } oldest)
                line += " " + text.Format("scoop.summary.oldest", Ago(oldest));

            return line;
        }
    }

    private DateTime? Oldest =>
        _read.Select(d => d.Oldest).Where(o => o is not null and not { Year: 1 }).DefaultIfEmpty(null).Min();

    private string Ago(DateTime when)
    {
        var text = _host.Text;
        var days = Math.Max(0, (int)(DateTime.Now - when).TotalDays);
        return days switch
        {
            0 => text["scoop.when.today"],
            < 30 => text.Format("scoop.when.days", days),
            < 365 => text.Format("scoop.when.months", days / 30),
            _ => text.Format("scoop.when.years", days / 365),
        };
    }

    public string Status
    {
        get => _status ?? _host.Text["scoop.status.ready"];
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

    // ---- Reading -----------------------------------------------------------------------------

    /// <summary>
    /// Reading every drive's bin is a directory listing per drive and a small file read per item,
    /// so it goes through the Tasks panel: on a bin holding thousands of rows it is not instant,
    /// and it is exactly the kind of work that should not freeze the window.
    /// </summary>
    private void Refresh()
    {
        if (_isReading)
            return;

        ErrorMessage = null;
        SetReading(true);
        CancelEmpty();

        _host.Background.Run(_host.Text["scoop.task"], async context =>
        {
            try
            {
                context.ReportProgress(null);
                context.Report(_host.Text["scoop.task.reading"]);
                var read = await Task.Run(() => RecycleBinContents.ReadAll(), context.Token);

                await Dispatcher.UIThread.InvokeAsync(() => Apply(read));
            }
            catch (OperationCanceledException)
            {
                await Dispatcher.UIThread.InvokeAsync(() => SetReading(false));
                throw;
            }
            catch (Exception ex)
            {
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    ErrorMessage = ex.Message;
                    SetReading(false);
                });
            }
        });
    }

    private void Apply(IReadOnlyList<BinDrive> read)
    {
        _read = read;
        _hasRead = true;

        var was = SelectedDrive?.Root ?? _settings.Drive;
        Drives.Clear();
        foreach (var drive in read)
            Drives.Add(new DriveViewModel(drive));

        // The drive that was open, else the fullest one, since that is the question being asked.
        _selectedDrive = Drives.FirstOrDefault(d => string.Equals(d.Root, was, StringComparison.OrdinalIgnoreCase))
                         ?? Drives.OrderByDescending(d => d.Drive.Bytes).FirstOrDefault();
        OnPropertyChanged(nameof(SelectedDrive));

        ShowItems();
        SetReading(false);

        Status = _host.Text.Format("scoop.status.refreshed", DateTime.Now.ToString("HH:mm"));
        OnPropertyChanged(nameof(HasDrives));
        OnPropertyChanged(nameof(SummaryText));
        OnPropertyChanged(nameof(DriveUnreadable));
        OnPropertyChanged(nameof(HasDriveTrouble));
    }

    /// <summary>Biggest first: the tab exists to answer where the room went.</summary>
    private void ShowItems()
    {
        var was = SelectedItem?.Item.DataPath;
        var now = DateTime.Now;

        Items.Clear();
        foreach (var item in (SelectedDrive?.Drive.Items ?? []).OrderByDescending(i => i.Size))
            Items.Add(new BinItemViewModel(item, now));

        SelectedItem = Items.FirstOrDefault(i => i.Item.DataPath == was);
        OnPropertyChanged(nameof(IsEmpty));
    }

    private void SetReading(bool reading)
    {
        _isReading = reading;
        OnPropertyChanged(nameof(IsReading));
        OnPropertyChanged(nameof(IsEmpty));
        RefreshCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
        EmptyCommand.RaiseCanExecuteChanged();
    }

    // ---- Putting one back --------------------------------------------------------------------

    private void Restore()
    {
        if (SelectedItem is not { } row)
            return;

        var failure = RecycleBinContents.Restore(row.Item);
        if (failure is not null)
        {
            ErrorMessage = _host.Text.Format("scoop.restore.failed", row.Name, failure);
            return;
        }

        ErrorMessage = null;
        Status = _host.Text.Format("scoop.restore.done", row.Name, row.Item.OriginalFolder);
        _host.Log($"Scoop restored {row.Item.OriginalPath} from the bin.");
        _host.Store.Record("restored", row.Item.OriginalPath, _host.Text["scoop.journal.restored"],
            new Dictionary<string, string> { ["size"] = row.Size.ToString() });

        Refresh();
    }

    private void ShowOrigin()
    {
        if (SelectedItem is not { } row)
            return;
        try
        {
            if (File.Exists(row.Item.OriginalPath) || Directory.Exists(row.Item.OriginalPath))
                Explorer.Reveal(row.Item.OriginalPath);
            else if (Directory.Exists(row.Item.OriginalFolder))
                Explorer.Open(row.Item.OriginalFolder);
            else
                Status = _host.Text.Format("scoop.origin.gone", row.Item.OriginalFolder);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    // ---- Emptying one drive ------------------------------------------------------------------

    /// <summary>
    /// The first press only changes the button's words; the second does it. The same two-click
    /// shape the Plugins tab uses for Uninstall, and for a stronger reason: this is the one
    /// action in Meows with nothing behind it. Per drive, never all of them at once.
    /// </summary>
    public bool IsConfirmingEmpty => _confirmingEmptyOf is not null && _confirmingEmptyOf == SelectedDrive?.Root;

    public string EmptyText => IsConfirmingEmpty
        ? _host.Text.Format("scoop.empty.sure", SelectedDrive?.Root ?? "")
        : _host.Text.Format("scoop.empty.drive", SelectedDrive?.Root ?? "");

    private void Empty()
    {
        if (SelectedDrive is not { } drive || drive.Drive.Count == 0)
            return;

        if (!IsConfirmingEmpty)
        {
            _confirmingEmptyOf = drive.Root;
            RaiseEmpty();
            return;
        }

        var bytes = drive.Drive.Bytes;
        var count = drive.Drive.Count;
        _confirmingEmptyOf = null;
        RaiseEmpty();

        var failure = RecycleBinContents.Empty(drive.Root);
        if (failure is not null)
        {
            ErrorMessage = _host.Text.Format("scoop.empty.failed", drive.Root, failure);
            return;
        }

        ErrorMessage = null;
        Status = _host.Text.Format("scoop.empty.done", drive.Root, RecycleBinContents.Humanise(bytes), count);
        _host.Log(LogLevel.Warning, $"Scoop emptied {drive.Root}: {count} item(s), {RecycleBinContents.Humanise(bytes)}, not recoverable.");
        _host.Store.Record("emptied", drive.Root, _host.Text.Format("scoop.journal.emptied", count),
            new Dictionary<string, string> { ["bytes"] = bytes.ToString(), ["items"] = count.ToString() });

        Refresh();
    }

    /// <summary>Back to the plain button, for when something else happened in between.</summary>
    public void CancelEmpty()
    {
        if (_confirmingEmptyOf is null)
            return;
        _confirmingEmptyOf = null;
        RaiseEmpty();
    }

    private void RaiseEmpty()
    {
        OnPropertyChanged(nameof(IsConfirmingEmpty));
        OnPropertyChanged(nameof(EmptyText));
    }

    // ---- The rest of the shell ----------------------------------------------------------------

    /// <summary>On the Home card: how much is lying about in bins, and where the oldest of it is from.</summary>
    public Glance? Glance()
    {
        if (!_hasRead || _read.Sum(d => d.Count) == 0)
            return null;

        // Never trouble. A full bin is room waiting to be had, not something that has gone wrong.
        return new Glance(SummaryText);
    }

    /// <summary>Ctrl+K into what is showing: answered from the rows already read.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        return _read
            .SelectMany(d => d.Items.Select(i => (Drive: d, Item: i)))
            .Where(row => SearchWords.Match(words, row.Item.Name, row.Item.OriginalFolder))
            .OrderByDescending(row => row.Item.Size)
            .Take(limit)
            .Select(row => new SearchHit(
                row.Item.Name,
                $"{RecycleBinContents.Humanise(row.Item.Size)} · {row.Item.OriginalFolder}",
                () => Select(row.Drive.Root, row.Item.DataPath)))
            .ToList();
    }

    private void Select(string root, string dataPath)
    {
        SelectedDrive = Drives.FirstOrDefault(d => d.Root == root) ?? SelectedDrive;
        SelectedItem = Items.FirstOrDefault(i => i.Item.DataPath == dataPath);
    }

    private void SaveSettings()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log($"Could not save Scoop settings: {ex.Message}");
        }
    }

    public void Dispose() => _language.Dispose();
}
