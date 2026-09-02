using System.Collections.ObjectModel;
using System.Diagnostics;
using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Litter.Services;

namespace Meows.Plugins.Litter.ViewModels;

public sealed class LitterSettings
{
    public string? Folder { get; set; }

    public string? MoveTo { get; set; }

    public bool ConfirmDeletes { get; set; } = true;
}

public sealed class ItemViewModel(LitterItem item) : ObservableObject
{
    public LitterItem Item { get; } = item;

    public string Name => Item.Name;

    public string Path => Item.Path;

    public string SizeText => LitterScan.Humanise(Item.Size);

    public string AgeText => Item.Days switch
    {
        0 => "today",
        1 => "yesterday",
        var d and < 30 => $"{d} days ago",
        var d and < 365 => $"{d / 30} months ago",
        var d => $"{d / 365} years ago",
    };

    public string KindText => Item.Kind.ToString();

    public string Glyph => Item.Kind switch
    {
        LitterKind.Installer => "⚙",
        LitterKind.Archive => "🗜",
        LitterKind.Image => "🖼",
        LitterKind.Video => "▶",
        LitterKind.Audio => "♪",
        LitterKind.Document => "📄",
        LitterKind.Unfinished => "⚠",
        _ => "📁",
    };

    /// <summary>A download that never finished is never worth keeping, so it is called out.</summary>
    public bool IsJunk => Item.Kind == LitterKind.Unfinished;
}

/// <summary>One filter button: a kind or an age, with what it would show.</summary>
public sealed class BucketViewModel(string name, string key, int count, long size) : ObservableObject
{
    private bool _isOn;

    public string Name { get; } = name;

    public string Key { get; } = key;

    public string Detail { get; } = count == 1
        ? $"1 item, {LitterScan.Humanise(size)}"
        : $"{count} items, {LitterScan.Humanise(size)}";

    public bool IsOn
    {
        get => _isOn;
        set => SetField(ref _isOn, value);
    }
}

public sealed class LitterViewModel : ObservableObject, IDisposable
{
    private readonly IMeowsHost _host;
    private LitterSettings _settings;
    private IReadOnlyList<LitterItem> _all = [];

    private string _status = "Nothing read yet.";
    private string? _errorMessage;
    private string? _activeFilter;
    private List<ItemViewModel> _pendingDelete = [];

    public LitterViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<LitterSettings>() ?? new LitterSettings();
        _settings.Folder ??= DefaultDownloads();

        RefreshCommand = new RelayCommand(Refresh);
        FilterCommand = new RelayCommand(p => ApplyFilter((p as BucketViewModel)?.Key));
        ClearFilterCommand = new RelayCommand(() => ApplyFilter(null));
        DeleteCommand = new RelayCommand(AskToDelete, () => Selected.Count > 0);
        ConfirmDeleteCommand = new RelayCommand(() => Remove(_pendingDelete), () => IsAsking);
        CancelDeleteCommand = new RelayCommand(() => PendingCount = 0, () => IsAsking);
        ExploreCommand = new RelayCommand(() => Open(SelectedOne?.Path), () => SelectedOne is not null);
        OpenFolderCommand = new RelayCommand(() => Open(Folder), () => Directory.Exists(Folder));

        Refresh();
    }

    public ObservableCollection<ItemViewModel> Items { get; } = new();

    public ObservableCollection<BucketViewModel> Buckets { get; } = new();

    public List<ItemViewModel> Selected { get; } = [];

    public RelayCommand RefreshCommand { get; }

    public RelayCommand FilterCommand { get; }

    public RelayCommand ClearFilterCommand { get; }

    public RelayCommand DeleteCommand { get; }

    public RelayCommand ConfirmDeleteCommand { get; }

    public RelayCommand CancelDeleteCommand { get; }

    public RelayCommand ExploreCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public string Folder => _settings.Folder ?? "";

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

    public bool IsEmpty => Items.Count == 0;

    public string Summary
    {
        get
        {
            if (_all.Count == 0)
                return "";
            var total = _all.Sum(i => i.Size);
            var junk = _all.Count(i => i.Kind == LitterKind.Unfinished);
            var text = $"{_all.Count} items, {LitterScan.Humanise(total)}";
            return junk > 0 ? $"{text}, {junk} unfinished" : text;
        }
    }

    public ItemViewModel? SelectedOne => Selected.Count == 1 ? Selected[0] : null;

    // ---- the safeguard, same shape as Chonk's ----

    public bool ConfirmDeletes
    {
        get => _settings.ConfirmDeletes;
        set
        {
            if (_settings.ConfirmDeletes == value)
                return;
            _settings.ConfirmDeletes = value;
            Save();
            OnPropertyChanged();
            OnPropertyChanged(nameof(DoNotAskAgain));
        }
    }

    public bool DoNotAskAgain
    {
        get => !ConfirmDeletes;
        set => ConfirmDeletes = !value;
    }

    private int _pendingCount;

    public int PendingCount
    {
        get => _pendingCount;
        private set
        {
            if (!SetField(ref _pendingCount, value))
                return;
            OnPropertyChanged(nameof(IsAsking));
            OnPropertyChanged(nameof(ConfirmPrompt));
            OnPropertyChanged(nameof(ConfirmDetail));
            ConfirmDeleteCommand.RaiseCanExecuteChanged();
            CancelDeleteCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsAsking => PendingCount > 0;

    public string ConfirmPrompt => PendingCount == 1
        ? $"Send {_pendingDelete.FirstOrDefault()?.Name} to the Recycle Bin?"
        : $"Send {PendingCount} items to the Recycle Bin?";

    public string ConfirmDetail
    {
        get
        {
            if (!IsAsking)
                return "";
            var size = _pendingDelete.Sum(i => i.Item.Size);
            var folders = _pendingDelete.Count(i => Directory.Exists(i.Path));
            var note = folders > 0
                ? $" {folders} of them are folders and go whole, with everything inside."
                : "";
            return $"{LitterScan.Humanise(size)} in total.{note} It can be brought back from the Recycle Bin.";
        }
    }

    public void SetSelection(IEnumerable<ItemViewModel> items)
    {
        Selected.Clear();
        Selected.AddRange(items);
        OnPropertyChanged(nameof(SelectedOne));
        OnPropertyChanged(nameof(SelectionText));
        DeleteCommand.RaiseCanExecuteChanged();
        ExploreCommand.RaiseCanExecuteChanged();
    }

    public string SelectionText => Selected.Count switch
    {
        0 => "",
        1 => Selected[0].SizeText,
        var n => $"{n} picked, {LitterScan.Humanise(Selected.Sum(i => i.Item.Size))}",
    };

    public void SetFolder(string folder)
    {
        _settings.Folder = folder;
        Save();
        OnPropertyChanged(nameof(Folder));
        Refresh();
    }

    public void Refresh()
    {
        ErrorMessage = null;

        if (!Directory.Exists(Folder))
        {
            ErrorMessage = $"{Folder} does not exist. Pick a folder.";
            _all = [];
            Rebuild();
            return;
        }

        _all = LitterScan.Read(Folder, DateTime.Now);
        BuildBuckets();
        Rebuild();
        Status = _all.Count == 0 ? "Nothing in here." : "";
        _host.Log($"Litter read {_all.Count} item(s) from {Folder}");
    }

    private void BuildBuckets()
    {
        Buckets.Clear();

        foreach (var age in Enum.GetValues<LitterAge>())
        {
            var matching = _all.Where(i => i.Age == age).ToList();
            if (matching.Count > 0)
                Buckets.Add(new BucketViewModel(LitterScan.Describe(age), $"age:{age}",
                    matching.Count, matching.Sum(i => i.Size)));
        }

        foreach (var kind in Enum.GetValues<LitterKind>())
        {
            var matching = _all.Where(i => i.Kind == kind).ToList();
            if (matching.Count > 0)
                Buckets.Add(new BucketViewModel(kind.ToString(), $"kind:{kind}",
                    matching.Count, matching.Sum(i => i.Size)));
        }
    }

    private void ApplyFilter(string? key)
    {
        _activeFilter = _activeFilter == key ? null : key;
        Rebuild();
    }

    private void Rebuild()
    {
        Items.Clear();

        var shown = _all.AsEnumerable();
        if (_activeFilter is { } filter)
        {
            var parts = filter.Split(':', 2);
            shown = parts[0] == "age"
                ? shown.Where(i => i.Age.ToString() == parts[1])
                : shown.Where(i => i.Kind.ToString() == parts[1]);
        }

        // Biggest first: the question in a downloads folder is always what is costing the most.
        foreach (var item in shown.OrderByDescending(i => i.Size))
            Items.Add(new ItemViewModel(item));

        foreach (var bucket in Buckets)
            bucket.IsOn = bucket.Key == _activeFilter;

        SetSelection([]);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
    }

    private void AskToDelete()
    {
        if (Selected.Count == 0)
            return;

        _pendingDelete = [.. Selected];

        if (!ConfirmDeletes)
        {
            Remove(_pendingDelete);
            return;
        }

        PendingCount = _pendingDelete.Count;
    }

    private void Remove(List<ItemViewModel> items)
    {
        PendingCount = 0;
        if (items.Count == 0)
            return;

        var freed = items.Sum(i => i.Item.Size);
        var outcome = RecycleBin.Send(items.Select(i => i.Path).ToList());

        if (!outcome.Succeeded)
        {
            ErrorMessage = outcome.FailureReason ?? "Nothing could be removed.";
            _host.Log($"Litter could not remove {items.Count} item(s): {ErrorMessage}");
            Refresh();
            return;
        }

        Status = $"Sent {outcome.Deleted} item(s) to the Recycle Bin, {LitterScan.Humanise(freed)} freed";
        _host.Log($"Litter sent {outcome.Deleted} item(s) to the Recycle Bin, {LitterScan.Humanise(freed)} freed");
        Refresh();
    }

    /// <summary>Where Windows keeps downloads, which is where this is nearly always pointed.</summary>
    private static string DefaultDownloads()
    {
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        return System.IO.Path.Combine(profile, "Downloads");
    }

    private void Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not open {path}: {ex.Message}";
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
            _host.Log($"Could not save Litter settings: {ex.Message}");
        }
    }

    public void Dispose()
    {
    }
}
