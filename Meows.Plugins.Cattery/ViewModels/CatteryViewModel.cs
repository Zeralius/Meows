using System.Collections.ObjectModel;
using System.Diagnostics;
using Avalonia.Threading;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Cattery.Services;

namespace Meows.Plugins.Cattery.ViewModels;

public sealed class CatterySettings
{
    /// <summary>The folders projects live under, Programmierung and the like.</summary>
    public List<string> Roots { get; set; } = [];

    public int SortIndex { get; set; }

    public int FilterIndex { get; set; }

    /// <summary>The last answer, so the tab opens on it while git is asked again.</summary>
    public List<RepoState> Last { get; set; } = [];

    public DateTime? LastReadUtc { get; set; }
}

/// <summary>One repository, as the list shows it.</summary>
public sealed class RepoViewModel(RepoState repo) : ObservableObject
{
    public RepoState Repo { get; } = repo;

    public string Name => Repo.Name;

    public string Path => Repo.Path;

    public string BranchText => Repo.Error is not null
        ? MeowsText.Current["cattery.error.short"]
        : Repo.Branch ?? MeowsText.Current["cattery.detached"];

    /// <summary>What is not committed, in one short phrase, or "clean".</summary>
    public string WorkText
    {
        get
        {
            if (Repo.Error is { } error)
                return error;
            var parts = new List<string>();
            if (Repo.Conflicted > 0)
                parts.Add(MeowsText.Current.Format("cattery.work.conflicted", Repo.Conflicted));
            if (Repo.Changed > 0)
                parts.Add(MeowsText.Current.Format("cattery.work.changed", Repo.Changed));
            if (Repo.Untracked > 0)
                parts.Add(MeowsText.Current.Format("cattery.work.untracked", Repo.Untracked));
            return parts.Count == 0 ? MeowsText.Current["cattery.work.clean"] : string.Join(", ", parts);
        }
    }

    public string RemoteText => Repo.Error is not null
        ? ""
        : Repo.Upstream is null
            ? MeowsText.Current["cattery.remote.none"]
            : (Repo.Ahead, Repo.Behind) switch
            {
                (> 0, > 0) => MeowsText.Current.Format("cattery.remote.both", Repo.Ahead, Repo.Behind),
                (> 0, _) => MeowsText.Current.Format("cattery.remote.ahead", Repo.Ahead),
                (_, > 0) => MeowsText.Current.Format("cattery.remote.behind", Repo.Behind),
                _ => MeowsText.Current["cattery.remote.even"],
            };

    public string LastCommitText => Repo.LastCommitUtc is { } when
        ? CatteryViewModel.Ago(when.ToLocalTime(), DateTime.Now)
        : MeowsText.Current["cattery.nocommits"];

    public bool IsDirty => Repo.IsDirty;

    public bool HasUnpushed => Repo.HasUnpushed;

    public bool HasError => Repo.Error is not null;

    internal void Reread() => OnEverythingChanged();
}

/// <summary>
/// Cattery: every git repository under the project folders, with its branch, what is not
/// committed, what is not pushed, and how long since anyone committed. Read-only; it asks git and
/// never tells it anything. The last answer is kept, so the tab opens on it while git is asked
/// again in the background.
/// </summary>
public sealed class CatteryViewModel : ObservableObject, IDisposable, ISearchable, IGlanceable
{
    public static readonly IReadOnlyList<string> Sorts = ["cattery.sort.neglect", "cattery.sort.name", "cattery.sort.lastcommit"];

    public static readonly IReadOnlyList<string> Filters = ["cattery.filter.all", "cattery.filter.dirty", "cattery.filter.unpushed"];

    private readonly IMeowsHost _host;
    private readonly Func<string?> _git;
    private readonly CatterySettings _settings;
    private readonly LanguageWatch _language;
    private IReadOnlyList<RepoState> _all;
    private IBackgroundTask? _scan;
    private RepoViewModel? _selected;
    private bool _isScanning;
    private string? _status;
    private string? _errorMessage;

    public CatteryViewModel(IMeowsHost host) : this(host, Repos.GitExecutable)
    {
    }

    /// <summary>With where git is supplied from elsewhere, which is how a test says there is none.</summary>
    public CatteryViewModel(IMeowsHost host, Func<string?> git)
    {
        _host = host;
        _git = git;
        _settings = host.LoadSettings<CatterySettings>() ?? new CatterySettings();
        _all = _settings.Last;

        RefreshCommand = new RelayCommand(Refresh, () => !IsScanning && _settings.Roots.Count > 0);
        AddRootCommand = new RelayCommand(() => _ = AddRootAsync());
        RemoveRootCommand = new RelayCommand(p => RemoveRoot(p as string));
        OpenFolderCommand = new RelayCommand(() => Open(Selected?.Path), () => Selected is not null);
        OpenTerminalCommand = new RelayCommand(() => Terminal(Selected?.Path), () => Selected is not null);

        _language = new LanguageWatch(() =>
        {
            OnEverythingChanged();
            foreach (var repo in Repositories)
                repo.Reread();
        });

        Rebuild();
        if (_settings.Roots.Count > 0)
            Refresh();
    }

    /// <summary>The list on screen.</summary>
    public ObservableCollection<RepoViewModel> Repositories { get; } = [];

    public IReadOnlyList<string> Roots => _settings.Roots.ToList();

    public RelayCommand RefreshCommand { get; }

    public RelayCommand AddRootCommand { get; }

    public RelayCommand RemoveRootCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand OpenTerminalCommand { get; }

    public IReadOnlyList<string> SortChoices => Sorts.Select(k => MeowsText.Current[k]).ToList();

    public IReadOnlyList<string> FilterChoices => Filters.Select(k => MeowsText.Current[k]).ToList();

    public int SortIndex
    {
        get => _settings.SortIndex;
        set
        {
            if (value < 0 || value >= Sorts.Count || value == _settings.SortIndex)
                return;
            _settings.SortIndex = value;
            SaveSettings();
            OnPropertyChanged();
            Rebuild();
        }
    }

    public int FilterIndex
    {
        get => _settings.FilterIndex;
        set
        {
            if (value < 0 || value >= Filters.Count || value == _settings.FilterIndex)
                return;
            _settings.FilterIndex = value;
            SaveSettings();
            OnPropertyChanged();
            Rebuild();
        }
    }

    public RepoViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value))
                return;
            OpenFolderCommand.RaiseCanExecuteChanged();
            OpenTerminalCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsScanning
    {
        get => _isScanning;
        private set
        {
            if (SetField(ref _isScanning, value))
                RefreshCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status ?? (_settings.Roots.Count == 0
            ? _host.Text["cattery.status.noroots"]
            : _settings.LastReadUtc is { } read
                ? _host.Text.Format("cattery.status.read", Ago(read.ToLocalTime(), DateTime.Now))
                : _host.Text["cattery.status.ready"]);
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

    public bool IsEmpty => Repositories.Count == 0;

    public string Summary
    {
        get
        {
            if (_all.Count == 0)
                return "";
            var dirty = _all.Count(r => r.IsDirty);
            var unpushed = _all.Count(r => r.HasUnpushed);
            return _host.Text.Format("cattery.summary", _all.Count, dirty, unpushed);
        }
    }

    private void Refresh()
    {
        if (IsScanning)
            return;
        var git = _git();
        if (git is null)
        {
            ErrorMessage = _host.Text["cattery.error.nogit"];
            return;
        }

        ErrorMessage = null;
        IsScanning = true;
        Status = _host.Text["cattery.status.looking"];
        var roots = _settings.Roots.ToList();

        _scan = _host.Background.Run(_host.Text["cattery.task"], async context =>
        {
            var found = await Task.Run(() => Read(git, roots, context.Token), context.Token);
            await Dispatcher.UIThread.InvokeAsync(() => Show(found));
        });
    }

    /// <summary>Finds and asks, a few repositories at a time: git status across forty is not instant.</summary>
    public static IReadOnlyList<RepoState> Read(string git, IReadOnlyList<string> roots, CancellationToken token)
    {
        var paths = roots.SelectMany(r => Repos.Find(r, token: token)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        var states = new RepoState[paths.Count];
        Parallel.For(0, paths.Count, new ParallelOptions { MaxDegreeOfParallelism = 4, CancellationToken = token },
            i => states[i] = Repos.Read(git, paths[i], token));
        return states;
    }

    /// <summary>Puts a finished read on screen and keeps it for next time. Separate so a test can run the read itself.</summary>
    public void Show(IReadOnlyList<RepoState> found)
    {
        IsScanning = false;
        _all = found;
        _settings.Last = found.ToList();
        _settings.LastReadUtc = DateTime.UtcNow;
        SaveSettings();
        _status = null;
        OnPropertyChanged(nameof(Status));
        Rebuild();
        _host.Log($"Cattery read {found.Count} repositor(ies).");
    }

    private void Rebuild()
    {
        IEnumerable<RepoState> shown = FilterIndex switch
        {
            1 => _all.Where(r => r.IsDirty || r.Conflicted > 0),
            2 => _all.Where(r => r.HasUnpushed),
            _ => _all,
        };

        shown = SortIndex switch
        {
            1 => shown.OrderBy(r => r.Name, StringComparer.CurrentCultureIgnoreCase),
            2 => shown.OrderByDescending(r => r.LastCommitUtc ?? DateTime.MinValue),
            // Neglect: work sitting uncommitted the longest comes first, then unpushed commits,
            // then everything else, oldest first. A clean, pushed repository is not neglected
            // however old it is; it is finished.
            _ => shown.OrderBy(r => r.Error is not null ? 3 : r.IsDirty ? 0 : r.HasUnpushed ? 1 : 2)
                .ThenBy(r => r.LastCommitUtc ?? DateTime.MinValue),
        };

        var keep = Selected?.Path;
        Repositories.Clear();
        foreach (var repo in shown)
            Repositories.Add(new RepoViewModel(repo));
        Selected = Repositories.FirstOrDefault(r => r.Path == keep);
        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
    }

    public static string Ago(DateTime when, DateTime now)
    {
        var span = now - when;
        return span.TotalDays switch
        {
            < 1 when span.TotalHours < 1 => MeowsText.Current["cattery.ago.now"],
            < 1 => MeowsText.Current.Format("cattery.ago.hours", (int)span.TotalHours),
            < 60 => MeowsText.Current.Format("cattery.ago.days", (int)span.TotalDays),
            < 730 => MeowsText.Current.Format("cattery.ago.months", (int)(span.TotalDays / 30.44)),
            _ => MeowsText.Current.Format("cattery.ago.years", (int)(span.TotalDays / 365.25)),
        };
    }

    private async Task AddRootAsync()
    {
        try
        {
            var picked = await _host.Pick.Folder(new PickOptions { Title = _host.Text["cattery.dialog.root"] });
            if (!string.IsNullOrWhiteSpace(picked))
                AddRoot(picked);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not pick: {ex.Message}");
        }
    }

    public void AddRoot(string folder)
    {
        if (_settings.Roots.Contains(folder, StringComparer.OrdinalIgnoreCase))
            return;
        _settings.Roots.Add(folder);
        SaveSettings();
        OnPropertyChanged(nameof(Roots));
        RefreshCommand.RaiseCanExecuteChanged();
        Refresh();
    }

    private void RemoveRoot(string? folder)
    {
        if (folder is null || _settings.Roots.RemoveAll(r => string.Equals(r, folder, StringComparison.OrdinalIgnoreCase)) == 0)
            return;
        _all = _all.Where(r => !r.Path.StartsWith(folder, StringComparison.OrdinalIgnoreCase)).ToList();
        _settings.Last = _all.ToList();
        SaveSettings();
        OnPropertyChanged(nameof(Roots));
        RefreshCommand.RaiseCanExecuteChanged();
        Rebuild();
    }

    private void Open(string? path)
    {
        try
        {
            if (path is not null && Directory.Exists(path))
                Explorer.Open(path);
        }
        catch (Exception ex)
        {
            ErrorMessage = ex.Message;
        }
    }

    /// <summary>Windows Terminal in the repository when it is there, a plain PowerShell when it is not.</summary>
    private void Terminal(string? path)
    {
        if (path is null || !Directory.Exists(path))
            return;
        try
        {
            var terminal = new ProcessStartInfo("wt.exe") { UseShellExecute = true };
            terminal.ArgumentList.Add("-d");
            terminal.ArgumentList.Add(path);
            Process.Start(terminal);
        }
        catch (Exception)
        {
            try
            {
                Process.Start(new ProcessStartInfo("powershell.exe") { WorkingDirectory = path, UseShellExecute = true });
            }
            catch (Exception ex)
            {
                ErrorMessage = _host.Text.Format("cattery.error.terminal", ex.Message);
            }
        }
    }

    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        return Repositories
            .Where(r => SearchWords.Match(words, r.Name, r.Path))
            .Take(limit)
            .Select(r => new SearchHit(r.Name, $"{r.BranchText} · {r.WorkText}", () => Selected = r))
            .ToList();
    }

    /// <summary>Home's line: how many have work sitting uncommitted, and the one sitting longest. A fact, not trouble.</summary>
    public Glance? Glance()
    {
        var dirty = _all.Where(r => r.IsDirty).OrderBy(r => r.LastCommitUtc ?? DateTime.MinValue).ToList();
        if (dirty.Count == 0)
            return null;
        return dirty[0].LastCommitUtc is { } when
            ? new Glance(_host.Text.Format("cattery.glance", dirty.Count, dirty[0].Name, Ago(when.ToLocalTime(), DateTime.Now)))
            : new Glance(_host.Text.Format("cattery.glance.plain", dirty.Count));
    }

    private void SaveSettings()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log($"Could not save Cattery settings: {ex.Message}");
        }
    }

    public void Dispose()
    {
        _language.Dispose();
        _scan?.Cancel();
    }
}
