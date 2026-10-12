using System.Collections.ObjectModel;
using System.Text.Json;
using Avalonia.Automation;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;

namespace Meows.Plugins.Abstractions;

/// <summary>
/// A plugin that has taken others in as pages of its own tab: Litter with Catnip and Mouser as
/// its pages, Chonk with Weigh-In. The main plugin keeps its id, its settings and everything it
/// had; each page it absorbed keeps its own code, its own view, and its own settings, in a folder
/// under the main plugin's named after the id it used to have.
///
/// Everything the shell asks of a plugin is answered from all the pages together: actions,
/// records, jobs, searching while off, the Home line. The shell reads <see cref="Absorbed"/> to
/// carry over what was the absorbed plugin's: switched on, in a persona, in a rule, its history.
///
/// Page plugins are not public classes, so the shell does not find them as plugins of their own.
/// Since contract 1.8.0.
/// </summary>
public abstract class PagedPlugin : IMeowsPlugin
{
    protected PagedPlugin(IMeowsPlugin main, params IMeowsPlugin[] absorbed)
    {
        Main = main;
        Absorbed = absorbed;
    }

    /// <summary>The plugin whose id, settings and data this one carries on as.</summary>
    public IMeowsPlugin Main { get; }

    /// <summary>The plugins it took in, each now a page under its old id.</summary>
    public IReadOnlyList<IMeowsPlugin> Absorbed { get; }

    private IEnumerable<IMeowsPlugin> All => [Main, .. Absorbed];

    public virtual string Id => Main.Id;

    public virtual string DisplayName => Main.DisplayName;

    public virtual string PlainName => Main.PlainName;

    public virtual string Description => Main.Description;

    public virtual string? Icon => Main.Icon;

    public virtual string? Category => Main.Category;

    public virtual PluginTopics Topics => All.Aggregate(PluginTopics.None, (topics, p) => topics | p.Topics);

    public IReadOnlyList<PluginAction> Actions => [.. All.SelectMany(p => p.Actions)];

    public IReadOnlyList<RecordedKind> Records => [.. All.SelectMany(p => p.Records).DistinctBy(r => r.Kind)];

    public IReadOnlyList<PluginJob> Jobs => [.. All.SelectMany(p => p.Jobs)];

    public Task<string> RunJob(string jobId, IMeowsJobHost host, CancellationToken token)
    {
        var owner = All.FirstOrDefault(p => p.Jobs.Any(j => j.Id == jobId))
                    ?? throw new JobDeclinedException($"No job called '{jobId}'.");
        return ReferenceEquals(owner, Main)
            ? Main.RunJob(jobId, host, token)
            : owner.RunJob(jobId, new NestedJobHost(host, owner.Id), token);
    }

    /// <summary>
    /// The id of the plugin a job came from: the absorbed one's for a job of a page, so a task
    /// scheduled as <c>Meows.exe --do weighin.measure</c> keeps working after Weigh-In became a
    /// page of Chonk.
    /// </summary>
    public string? OwnerOf(string jobId) => All.FirstOrDefault(p => p.Jobs.Any(j => j.Id == jobId))?.Id;

    public ISearchable? WhileOff(IMeowsDormantHost host)
    {
        var each = All.Select(p => ReferenceEquals(p, Main) ? p.WhileOff(host) : p.WhileOff(new NestedDormantHost(host, p.Id)))
            .OfType<ISearchable>()
            .ToList();
        return each.Count == 0 ? null : new SearchAll(each);
    }

    public Glance? GlanceWhileOff(IMeowsDormantHost host)
    {
        var each = All.Select(p => ReferenceEquals(p, Main) ? p.GlanceWhileOff(host) : p.GlanceWhileOff(new NestedDormantHost(host, p.Id)))
            .OfType<Glance>()
            .ToList();
        return each.FirstOrDefault(g => g.IsTrouble) ?? each.FirstOrDefault();
    }

    public Control CreateView(IMeowsHost host)
    {
        var model = new PagedViewModel();
        model.Add(Main, Main.CreateView(host));
        foreach (var page in Absorbed)
            model.Add(page, page.CreateView(new NestedHost(host, page.Id, model)));
        return new PagedView(model);
    }

    private sealed class SearchAll(IReadOnlyList<ISearchable> each) : ISearchable
    {
        public IReadOnlyList<SearchHit> Search(string query, int limit) =>
            [.. each.SelectMany(s => s.Search(query, limit)).Take(limit)];
    }
}

/// <summary>One page of a <see cref="PagedPlugin"/>'s tab: the plugin it was, and its view.</summary>
public sealed class PluginPage(IMeowsPlugin plugin, Control view) : ObservableObject
{
    public IMeowsPlugin Plugin { get; } = plugin;

    public Control View { get; } = view;

    /// <summary>What the page's view model is, for asking it things.</summary>
    public object? Model => View.DataContext;

    /// <summary>The page's name, under whichever name the switch on the Settings tab shows.</summary>
    public string Title => PluginNames.For(Plugin);

    internal void Retitle() => OnPropertyChanged(nameof(Title));
}

/// <summary>
/// The pages of a <see cref="PagedPlugin"/>, and the one in front. Everything the shell asks a
/// plugin's view model, it asks this, and this asks the pages: a search goes to all of them and
/// a hit brings its page to the front, a rule's action goes to the page that offers it.
/// </summary>
public sealed class PagedViewModel : ObservableObject, IDisposable, ISearchable, IMultiGlance, IGlanceable,
    IActionTarget, IUndoTarget, IHandoffTarget
{
    private PluginPage? _selected;
    private readonly LanguageWatch _language;

    public PagedViewModel()
    {
        _language = new LanguageWatch(Retitle);
        PluginNames.Changed += Retitle;
    }

    public ObservableCollection<PluginPage> Pages { get; } = [];

    public PluginPage? Selected
    {
        get => _selected;
        set => SetField(ref _selected, value);
    }

    internal void Add(IMeowsPlugin plugin, Control view)
    {
        var page = new PluginPage(plugin, view);
        Pages.Add(page);
        Selected ??= page;
    }

    private void Retitle()
    {
        foreach (var page in Pages)
            page.Retitle();
    }

    private void Show(PluginPage page) => Selected = page;

    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var hits = new List<SearchHit>();
        foreach (var page in Pages)
        {
            if (page.Model is not ISearchable searchable)
                continue;
            IReadOnlyList<SearchHit> found;
            try
            {
                found = searchable.Search(query, limit);
            }
            catch (Exception)
            {
                continue;
            }
            hits.AddRange(found.Select(h => h with
            {
                Open = () =>
                {
                    Show(page);
                    h.Open();
                },
            }));
        }
        return [.. hits.Take(limit)];
    }

    /// <summary>Up to three lines from all the pages, the ones wanting attention first.</summary>
    public IReadOnlyList<Glance> Glances()
    {
        var lines = new List<Glance>();
        foreach (var model in Pages.Select(p => p.Model))
        {
            try
            {
                if (model is IMultiGlance many)
                    lines.AddRange(many.Glances());
                else if (model is IGlanceable one && one.Glance() is { } glance)
                    lines.Add(glance);
            }
            catch (Exception)
            {
                // One page failing to say something should not silence the rest.
            }
        }
        return [.. lines.OrderByDescending(g => g.IsTrouble).Take(3)];
    }

    /// <summary>The one line, for whatever asks for only one: the first of <see cref="Glances"/>.</summary>
    public Glance? Glance() => Glances().FirstOrDefault();

    public Task<string> Perform(ActionRequest request, CancellationToken token)
    {
        var page = Pages.FirstOrDefault(p => p.Plugin.Actions.Any(a => a.Id == request.Action));
        if (page?.Model is not IActionTarget target)
            throw new ActionDeclinedException($"No page here does '{request.Action}'.");
        return target.Perform(request, token);
    }

    public bool CanUndo(StoredEvent stored) =>
        Pages.Any(p => p.Model is IUndoTarget target && Safely(() => target.CanUndo(stored)));

    public string? Undo(StoredEvent stored)
    {
        var page = Pages.FirstOrDefault(p => p.Model is IUndoTarget target && Safely(() => target.CanUndo(stored)));
        if (page?.Model is not IUndoTarget undo)
            return null;
        Show(page);
        return undo.Undo(stored);
    }

    public bool Accepts(Handoff handoff) =>
        Pages.Any(p => p.Model is IHandoffTarget target && Safely(() => target.Accepts(handoff)));

    public void Receive(Handoff handoff)
    {
        var page = Pages.FirstOrDefault(p => p.Model is IHandoffTarget target && Safely(() => target.Accepts(handoff)));
        if (page?.Model is not IHandoffTarget receiver)
            return;
        Show(page);
        receiver.Receive(handoff);
    }

    private static bool Safely(Func<bool> ask)
    {
        try
        {
            return ask();
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Dispose()
    {
        PluginNames.Changed -= Retitle;
        _language.Dispose();
        foreach (var page in Pages)
        {
            (page.View as IDisposable)?.Dispose();
            if (page.Model is IDisposable model && !ReferenceEquals(model, page.View))
                model.Dispose();
        }
    }
}

/// <summary>
/// The tab of a <see cref="PagedPlugin"/>: one button per page along the top, and the page in
/// front under them. Built in code, so the contract needs no XAML of its own.
/// </summary>
public sealed class PagedView : UserControl, IDisposable
{
    private readonly PagedViewModel _model;
    private readonly List<(PluginPage Page, ToggleButton Button)> _buttons = [];
    private readonly ContentControl _content = new();

    public PagedView(PagedViewModel model)
    {
        _model = model;
        DataContext = model;

        var strip = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 6 };
        foreach (var page in model.Pages)
        {
            var button = new ToggleButton { Padding = new Avalonia.Thickness(12, 4), FontSize = 12 };
            button.Click += (_, _) => model.Selected = page;
            page.PropertyChanged += (_, _) => Title(button, page);
            Title(button, page);
            strip.Children.Add(button);
            _buttons.Add((page, button));
        }

        var bar = new Border
        {
            Padding = new Avalonia.Thickness(14, 8),
            BorderThickness = new Avalonia.Thickness(0, 0, 0, 1),
            Child = strip,
            IsVisible = model.Pages.Count > 1,
        };
        bar.Bind(Border.BackgroundProperty, bar.GetResourceObservable("MeowsBar"));
        bar.Bind(Border.BorderBrushProperty, bar.GetResourceObservable("MeowsLineSoft"));
        DockPanel.SetDock(bar, Dock.Top);

        var dock = new DockPanel();
        dock.Children.Add(bar);
        dock.Children.Add(_content);
        Content = dock;

        model.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName == nameof(PagedViewModel.Selected))
                ShowSelected();
        };
        ShowSelected();
    }

    private static void Title(ToggleButton button, PluginPage page)
    {
        button.Content = page.Title;
        AutomationProperties.SetName(button, page.Title);
    }

    private void ShowSelected()
    {
        foreach (var (page, button) in _buttons)
            button.IsChecked = ReferenceEquals(page, _model.Selected);
        _content.Content = _model.Selected?.View;
    }

    public void Dispose() => _model.Dispose();
}

/// <summary>
/// The host a page of a <see cref="PagedPlugin"/> is given: the main plugin's own, but with a data
/// folder, settings and secrets of the page's own, under the id it had before it was a page. A
/// handoff to the main plugin from one of its pages goes to the page that takes it, in place.
/// </summary>
public sealed class NestedHost(IMeowsHost outer, string pageId, IHandoffTarget? local = null) : IMeowsHost
{
    private readonly NestedFiles _files = new(outer.DataDirectory, pageId);

    public string PluginId => outer.PluginId;

    public string DataDirectory => _files.Directory;

    public void Log(string message) => outer.Log(message);

    public void Log(LogLevel level, string message) => outer.Log(level, message);

    public IMeowsNotifications Notifications => outer.Notifications;

    public IMeowsBackgroundWork Background => outer.Background;

    public T? LoadSettings<T>() where T : class => _files.Load<T>();

    public void SaveSettings<T>(T settings) where T : class => _files.Save(settings);

    public IMeowsText Text => outer.Text;

    public IMeowsSecrets Secrets { get; } = new PrefixedSecrets(outer.Secrets, pageId);

    public IMeowsHandoff Handoff { get; } = new LocalHandoff(outer.Handoff, outer.PluginId, local);

    public IMeowsStore Store { get; } = new PrefixedStore(outer.Store, pageId);

    public IMeowsWatches Watches => outer.Watches;

    public IMeowsPicker Pick => outer.Pick;

    public IMeowsReach Reach => outer.Reach;

    public bool RunsFromOutside(string jobId) => outer.RunsFromOutside(jobId);
}

/// <summary>The same as <see cref="NestedHost"/>, for a page being searched while its plugin is off.</summary>
public sealed class NestedDormantHost(IMeowsDormantHost outer, string pageId) : IMeowsDormantHost
{
    private readonly NestedFiles _files = new(outer.DataDirectory, pageId);

    public string PluginId => outer.PluginId;

    public string DataDirectory => _files.Directory;

    public T? LoadSettings<T>() where T : class => _files.Load<T>();

    public IMeowsText Text => outer.Text;

    public IMeowsStore Store { get; } = new PrefixedStore(outer.Store, pageId);

    public IMeowsHandoff Handoff => outer.Handoff;
}

/// <summary>The same as <see cref="NestedHost"/>, for a page's job run with no window.</summary>
public sealed class NestedJobHost(IMeowsJobHost outer, string pageId) : IMeowsJobHost
{
    private readonly NestedFiles _files = new(outer.DataDirectory, pageId);

    public string PluginId => outer.PluginId;

    public string DataDirectory => _files.Directory;

    public T? LoadSettings<T>() where T : class => _files.Load<T>();

    public void SaveSettings<T>(T settings) where T : class => _files.Save(settings);

    public IMeowsText Text => outer.Text;

    public IMeowsStore Store { get; } = new PrefixedStore(outer.Store, pageId);

    public IMeowsHandoff Handoff => outer.Handoff;

    public void Log(string message) => outer.Log(message);

    public void Report(string status) => outer.Report(status);

    public void Notify(string title, string text, bool isTrouble = false) => outer.Notify(title, text, isTrouble);
}

/// <summary>A page's folder and its settings.json, written the way the shell writes them.</summary>
internal sealed class NestedFiles
{
    private static readonly JsonSerializerOptions Json = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    };

    public NestedFiles(string outer, string pageId)
    {
        var safe = string.Concat(pageId.Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
        Directory = Path.Combine(outer, safe);
        System.IO.Directory.CreateDirectory(Directory);
    }

    public string Directory { get; }

    private string File => Path.Combine(Directory, "settings.json");

    public T? Load<T>() where T : class
    {
        try
        {
            return System.IO.File.Exists(File) ? JsonSerializer.Deserialize<T>(System.IO.File.ReadAllText(File), Json) : null;
        }
        catch (Exception)
        {
            return null;
        }
    }

    public void Save<T>(T settings) where T : class
    {
        var temporary = $"{File}.{Guid.NewGuid():N}.writing";
        System.IO.File.WriteAllText(temporary, JsonSerializer.Serialize(settings, Json));
        System.IO.File.Move(temporary, File, overwrite: true);
    }
}

/// <summary>A page's secrets under its own names, so two pages cannot read each other's.</summary>
internal sealed class PrefixedSecrets(IMeowsSecrets outer, string pageId) : IMeowsSecrets
{
    private string Name(string name) => $"{pageId}.{name}";

    public bool Has(string name) => outer.Has(Name(name));

    public string? Get(string name) => outer.Get(Name(name));

    public void Set(string name, string value) => outer.Set(Name(name), value);

    public void Forget(string name) => outer.Forget(Name(name));
}

/// <summary>
/// The main plugin's store, with a page's small facts under names of its own. Its history is the
/// plugin's history: one tab, one journal.
/// </summary>
internal sealed class PrefixedStore(IMeowsStore outer, string pageId) : IMeowsStore
{
    private string Key(string key) => $"{pageId}:{key}";

    public void Record(string kind, string subject, string? detail = null, IReadOnlyDictionary<string, string>? data = null) =>
        outer.Record(kind, subject, detail, data);

    public IReadOnlyList<StoredEvent> Recent(int limit = 100, string? kind = null) => outer.Recent(limit, kind);

    public IReadOnlyList<StoredEvent> Search(string text, int limit = 100) => outer.Search(text, limit);

    public string? Get(string key) => outer.Get(Key(key));

    public void Set(string key, string value) => outer.Set(Key(key), value);

    public void Remove(string key) => outer.Remove(Key(key));

    public void MarkSeen(string hash, string? note = null) => outer.MarkSeen(hash, note);

    public SeenRecord? Seen(string hash) => outer.Seen(hash);
}

/// <summary>
/// A page sending to the plugin it is now part of, Weigh-In to Chonk, is answered in place by the
/// page that takes it; anything else goes out through the shell as before.
/// </summary>
internal sealed class LocalHandoff(IMeowsHandoff outer, string ownId, IHandoffTarget? local) : IMeowsHandoff
{
    public bool CanReach(string pluginId) =>
        string.Equals(pluginId, ownId, StringComparison.OrdinalIgnoreCase) ? local is not null : outer.CanReach(pluginId);

    public bool Send(string pluginId, Handoff handoff)
    {
        if (!string.Equals(pluginId, ownId, StringComparison.OrdinalIgnoreCase))
            return outer.Send(pluginId, handoff);
        if (local is null || !local.Accepts(handoff))
            return false;
        local.Receive(handoff);
        return true;
    }
}
