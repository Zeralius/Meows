using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Pantry.Services;

namespace Meows.Plugins.Pantry.ViewModels;

public sealed class PantrySettings
{
    public List<Recipe> Recipes { get; set; } = [];

    public List<StockItem> Stock { get; set; } = [];

    public List<PlanSlot> Plan { get; set; } = [];
}

/// <summary>One choice in a dropdown, named by the language the window is in.</summary>
public sealed class Choice(string key, int value) : ObservableObject
{
    public int Value { get; } = value;

    public string Name => MeowsText.Current[key];

    internal void Reread() => OnPropertyChanged(nameof(Name));
}

/// <summary>One recipe in a dropdown. Null means the day is empty.</summary>
public sealed class RecipeChoice(string? id, string shown)
{
    public string? Id { get; } = id;

    public string Shown { get; } = shown;
}

public sealed class RecipeRowViewModel : ObservableObject
{
    private readonly PantryViewModel _owner;

    public RecipeRowViewModel(Recipe recipe, PantryViewModel owner)
    {
        Recipe = recipe;
        _owner = owner;
    }

    public Recipe Recipe { get; }

    public string Id => Recipe.Id;

    public string Title
    {
        get => Recipe.Title;
        set
        {
            if (Recipe.Title == value)
                return;
            Recipe.Title = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(Shown));
            _owner.Touched();
        }
    }

    public string Shown => Recipe.Title.Trim().Length > 0
        ? Recipe.Title
        : MeowsText.Current["pantry.untitled"];

    public string Body
    {
        get => Recipe.Body;
        set
        {
            if (Recipe.Body == value)
                return;
            Recipe.Body = value ?? "";
            OnPropertyChanged();
            _owner.Touched();
        }
    }

    /// <summary>Minutes as typed. Anything that is not a number is untimed rather than an error.</summary>
    public string MinutesText
    {
        get => Recipe.Minutes <= 0 ? "" : Recipe.Minutes.ToString(CultureInfo.CurrentCulture);
        set
        {
            var minutes = int.TryParse(value, CultureInfo.CurrentCulture, out var parsed) && parsed > 0 ? parsed : 0;
            if (Recipe.Minutes == minutes)
                return;
            Recipe.Minutes = minutes;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TimeText));
            _owner.Touched();
        }
    }

    public string TimeText => Cookbook.TimeText(Recipe.Minutes, MeowsText.Current);

    /// <summary>Tags as typed, comma separated. What the search reads.</summary>
    public string TagsText
    {
        get => string.Join(", ", Recipe.Tags);
        set
        {
            var tags = (value ?? "").Split([',', ';'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();
            if (Recipe.Tags.SequenceEqual(tags, StringComparer.CurrentCultureIgnoreCase))
                return;
            Recipe.Tags = tags;
            OnPropertyChanged();
            _owner.Touched();
        }
    }

    internal void Reread() => OnEverythingChanged();
}

public sealed class StockRowViewModel : ObservableObject
{
    private readonly PantryViewModel _owner;

    public StockRowViewModel(StockItem item, PantryViewModel owner)
    {
        Item = item;
        _owner = owner;
    }

    public StockItem Item { get; }

    public string Id => Item.Id;

    public string Name
    {
        get => Item.Name;
        set
        {
            if (Item.Name == value)
                return;
            Item.Name = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(Shown));
            _owner.Touched();
        }
    }

    public string Shown => Item.Name.Trim().Length > 0
        ? Item.Name
        : MeowsText.Current["pantry.untitled"];

    public bool HasNote => !string.IsNullOrWhiteSpace(Item.Note);

    public string Note
    {
        get => Item.Note;
        set
        {
            if (Item.Note == value)
                return;
            Item.Note = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasNote));
            _owner.Touched();
        }
    }

    /// <summary>
    /// The date as the picker wants it. Avalonia hands out a <see cref="DateTimeOffset"/>; only
    /// the day is kept, because an offset would put a date into a different day depending on
    /// where the machine thinks it is.
    /// </summary>
    public DateTimeOffset? ExpiresOn
    {
        get => new DateTimeOffset(DateTime.SpecifyKind(Item.Expires.Date, DateTimeKind.Unspecified), TimeSpan.Zero);
        set
        {
            var date = (value?.Date ?? DateTime.Today).Date;
            if (Item.Expires.Date == date)
                return;

            Item.Expires = date;
            OnPropertyChanged();
            Reread();
            _owner.Touched();
        }
    }

    public bool IsExpired => Item.Expires.Date < DateTime.Today;

    public bool IsSoon => !IsExpired && Item.Expires.Date <= DateTime.Today.AddDays(Cookbook.LeadDays);

    public string ExpiryText => Cookbook.ExpiryText(Item, DateTime.Today, MeowsText.Current, PantryViewModel.Culture);

    internal void Reread() => OnEverythingChanged();
}

public sealed class PlanRowViewModel : ObservableObject
{
    private readonly PantryViewModel _owner;

    public PlanRowViewModel(PlanSlot slot, PantryViewModel owner)
    {
        Slot = slot;
        _owner = owner;
    }

    public PlanSlot Slot { get; }

    public DateTime Date => Slot.Date.Date;

    public string DateText => Slot.Date.ToString("dddd, d. MMM", PantryViewModel.Culture);

    public bool IsToday => Slot.Date.Date == DateTime.Today;

    /// <summary>The recipes as this row's dropdown: the empty day first, then the box by name.</summary>
    public IReadOnlyList<RecipeChoice> Options => _owner.PlanOptions;

    public RecipeChoice? PlannedChoice
    {
        get => Options.FirstOrDefault(o => o.Id == Slot.RecipeId) ?? Options.FirstOrDefault();
        set
        {
            var id = value?.Id;
            if (Slot.RecipeId == id)
                return;
            Slot.RecipeId = id;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PlannedText));
            _owner.Touched();
        }
    }

    public string PlannedText => PlannedChoice?.Id is null
        ? MeowsText.Current["pantry.plan.empty"]
        : PlannedChoice.Shown;

    internal void Reread() => OnEverythingChanged();
}

public sealed class PantryViewModel : ObservableObject, IDisposable, ISearchable, IHandoffTarget, IGlanceable, IActionTarget
{
    /// <summary>The condition key. One per plugin scope, so it replaces rather than stacks.</summary>
    private const string StockKey = "stock";

    private readonly IMeowsHost _host;
    private readonly PantrySettings _settings;
    private readonly LanguageWatch _language;
    private readonly Random _random = new();
    private readonly IBackgroundTask? _watch;

    private RecipeRowViewModel? _selectedRecipe;
    private StockRowViewModel? _selectedStock;
    private PlanRowViewModel? _selectedDay;
    private string? _status;
    private string? _errorMessage;
    private string _newStockName = "";

    public PantryViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<PantrySettings>() ?? new PantrySettings();
        Cookbook.RollPlan(_settings.Plan, DateTime.Today);
        Save();

        AddRecipeCommand = new RelayCommand(AddRecipe);
        DeleteRecipeCommand = new RelayCommand(DeleteRecipe, () => SelectedRecipe is not null);
        SuggestCommand = new RelayCommand(Suggest);
        ImportCommand = new RelayCommand(() => _ = ImportAsync());
        AddStockCommand = new RelayCommand(AddStock, () => !string.IsNullOrWhiteSpace(NewStockName));
        UseUpCommand = new RelayCommand(UseUp, () => SelectedStock is not null);
        CookCommand = new RelayCommand(Cook, () => SelectedDay?.Slot.RecipeId is not null);
        ClearDayCommand = new RelayCommand(ClearDay, () => SelectedDay?.Slot.RecipeId is not null);
        RefreshCommand = new RelayCommand(Refresh);

        RebuildAll();

        // Nothing here is expensive; the point of the timer is the calendar. A tab left open
        // across midnight would otherwise still be showing yesterday's arithmetic and would
        // never raise the thing that ran out while it sat there.
        _watch = host.Background.Schedule(
            host.Text["pantry.task.watch"], TimeSpan.FromHours(6),
            async context =>
            {
                if (context.Token.IsCancellationRequested)
                    return;

                await Dispatcher.UIThread.InvokeAsync(Refresh);
            },
            runImmediately: false);

        _language = new LanguageWatch(Retranslate);
    }

    /// <summary>Dates as the window's language writes them, not as the machine does.</summary>
    public static CultureInfo Culture => MeowsText.Current.Language == "de"
        ? CultureInfo.GetCultureInfo("de-DE")
        : CultureInfo.GetCultureInfo("en-GB");

    public ObservableCollection<RecipeRowViewModel> Recipes { get; } = [];

    public ObservableCollection<StockRowViewModel> Stock { get; } = [];

    public ObservableCollection<PlanRowViewModel> Plan { get; } = [];

    /// <summary>The recipes as plan rows: the empty day first, then the box by name.</summary>
    public ObservableCollection<RecipeChoice> PlanOptions { get; } = [];

    public RelayCommand AddRecipeCommand { get; }

    public RelayCommand DeleteRecipeCommand { get; }

    public RelayCommand SuggestCommand { get; }

    public RelayCommand ImportCommand { get; }

    public RelayCommand AddStockCommand { get; }

    public RelayCommand UseUpCommand { get; }

    public RelayCommand CookCommand { get; }

    public RelayCommand ClearDayCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RecipeRowViewModel? SelectedRecipe
    {
        get => _selectedRecipe;
        set
        {
            if (!SetField(ref _selectedRecipe, value))
                return;
            OnPropertyChanged(nameof(HasRecipe));
            DeleteRecipeCommand.RaiseCanExecuteChanged();
        }
    }

    public StockRowViewModel? SelectedStock
    {
        get => _selectedStock;
        set
        {
            if (!SetField(ref _selectedStock, value))
                return;
            OnPropertyChanged(nameof(HasStock));
            UseUpCommand.RaiseCanExecuteChanged();
        }
    }

    public PlanRowViewModel? SelectedDay
    {
        get => _selectedDay;
        set
        {
            if (!SetField(ref _selectedDay, value))
                return;
            OnPropertyChanged(nameof(HasDay));
            CookCommand.RaiseCanExecuteChanged();
            ClearDayCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasRecipe => SelectedRecipe is not null;

    public bool HasStock => SelectedStock is not null;

    public bool HasDay => SelectedDay is not null;

    public string NewStockName
    {
        get => _newStockName;
        set
        {
            if (SetField(ref _newStockName, value))
                AddStockCommand.RaiseCanExecuteChanged();
        }
    }

    public string Status
    {
        get => _status ?? _host.Text["pantry.status.ready"];
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

    /// <summary>The line under the header: what ran out, what is close, and what tonight is.</summary>
    public string Summary => Cookbook.SummaryOf(_settings.Stock, _settings.Plan, _settings.Recipes, DateTime.Today, _host.Text);

    /// <summary>The same line on the Home tab, red while something ran out or is close.</summary>
    public Glance? Glance() => Cookbook.GlanceOf(_settings.Stock, _settings.Plan, _settings.Recipes, DateTime.Today, _host.Text);

    /// <summary>Adds a recipe and selects it, because the next thing wanted is to write it.</summary>
    public void AddRecipe()
    {
        var recipe = new Recipe { Title = _host.Text["pantry.recipe.new"] };
        _settings.Recipes.Add(recipe);
        _host.Store.Record("added", recipe.Title);
        Save();
        RebuildAll();
        SelectedRecipe = Recipes.FirstOrDefault(r => r.Id == recipe.Id);
        Status = _host.Text.Format("pantry.status.added", recipe.Title);
    }

    private void DeleteRecipe()
    {
        if (SelectedRecipe is not { } selected)
            return;

        // No confirmation, deliberately. This is text in a settings file, not a file on disk.
        var name = selected.Shown;
        _settings.Recipes.RemoveAll(r => r.Id == selected.Id);
        foreach (var slot in _settings.Plan.Where(s => s.RecipeId == selected.Id))
            slot.RecipeId = null;
        Save();
        SelectedRecipe = null;
        RebuildAll();
        Status = _host.Text.Format("pantry.status.deleted", name);
    }

    /// <summary>Tonight's suggestion: one recipe drawn from the box, written into today's plan.</summary>
    public void Suggest()
    {
        var picked = Cookbook.Suggest(_settings.Recipes, _random);
        if (picked is null)
        {
            Status = _host.Text["pantry.status.emptybox"];
            return;
        }

        var today = _settings.Plan.FirstOrDefault(s => s.Date.Date == DateTime.Today);
        if (today is not null)
            today.RecipeId = picked.Id;
        _host.Store.Record("suggested", picked.Title);
        Save();
        RebuildAll();
        SelectedDay = Plan.FirstOrDefault(p => p.Date == DateTime.Today);
        Status = _host.Text.Format("pantry.status.suggested", picked.Title);
    }

    /// <summary>A recipe file becomes a recipe: the file name for a title, the text for the body.</summary>
    public void AddFromFile(string path)
    {
        string body;
        try
        {
            body = File.ReadAllText(path);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("pantry.error.open", path, ex.Message);
            return;
        }

        ErrorMessage = null;
        var name = Path.GetFileNameWithoutExtension(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (string.IsNullOrWhiteSpace(name))
            name = _host.Text["pantry.untitled"];

        var recipe = new Recipe { Title = name.Trim(), Body = body };
        _settings.Recipes.Add(recipe);
        _host.Store.Record("added", recipe.Title);
        Save();
        RebuildAll();
        SelectedRecipe = Recipes.FirstOrDefault(r => r.Id == recipe.Id);
        Status = _host.Text.Format("pantry.status.added", recipe.Title);
    }

    public void AddStock()
    {
        var name = NewStockName.Trim();
        if (name.Length == 0)
            return;

        var item = new StockItem { Name = name, Expires = DateTime.Today };
        _settings.Stock.Add(item);
        NewStockName = "";
        Save();
        RebuildAll();
        SelectedStock = Stock.FirstOrDefault(s => s.Id == item.Id);
        Status = _host.Text.Format("pantry.status.stocked", name);
    }

    private void UseUp()
    {
        if (SelectedStock is not { } selected)
            return;

        // Used, not deleted: the wording matters, the mechanics do not.
        var name = selected.Shown;
        _settings.Stock.RemoveAll(s => s.Id == selected.Id);
        Save();
        SelectedStock = null;
        RebuildAll();
        Status = _host.Text.Format("pantry.status.used", name);
    }

    private void Cook()
    {
        if (SelectedDay is not { } day || day.Slot.RecipeId is not { } id)
            return;

        var recipe = _settings.Recipes.FirstOrDefault(r => r.Id == id);
        var name = recipe?.Title.Trim();
        if (string.IsNullOrEmpty(name))
            name = _host.Text["pantry.untitled"];

        _host.Store.Record("cooked", name, day.Date.ToString("d", Culture));
        Status = _host.Text.Format("pantry.status.cooked", name);
    }

    private void ClearDay()
    {
        if (SelectedDay is not { } day)
            return;

        day.Slot.RecipeId = null;
        Save();
        day.Reread();
        OnPropertyChanged(nameof(Summary));
    }

    /// <summary>
    /// A rule's "suggest tonight": fills tonight when it is empty, and declines when the plan
    /// already says. Nothing is selected, since nobody is necessarily looking.
    /// </summary>
    public Task<string> Perform(ActionRequest request, CancellationToken token)
    {
        if (request.Action != PantryPlugin.SuggestAction)
            throw new ActionDeclinedException(_host.Text.Format("pantry.action.unknown", request.Action));

        var today = _settings.Plan.FirstOrDefault(s => s.Date.Date == DateTime.Today);
        if (today?.RecipeId is not null)
            throw new ActionDeclinedException(_host.Text["pantry.action.already"]);

        var picked = Cookbook.Suggest(_settings.Recipes, _random);
        if (picked is null)
            throw new ActionDeclinedException(_host.Text["pantry.status.emptybox"]);

        if (today is not null)
            today.RecipeId = picked.Id;
        _host.Store.Record("suggested", picked.Title);
        Save();
        RebuildAll();

        return Task.FromResult(_host.Text.Format("pantry.status.suggested", picked.Title));
    }

    public bool Accepts(Handoff handoff)
    {
        if (handoff.Verb == PantryPlugin.ShowVerb && handoff.Note is { Length: > 0 })
            return true;

        if (handoff.Verb == HandoffVerbs.Files && handoff.Paths.Count > 0)
            return handoff.Paths.All(p => File.Exists(p) &&
                (p.EndsWith(".md", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".txt", StringComparison.OrdinalIgnoreCase)));

        return false;
    }

    public void Receive(Handoff handoff)
    {
        if (!Accepts(handoff))
            return;

        // From Ctrl+K while Pantry was off: the hit, now that there is a list to select in.
        if (handoff.Verb == PantryPlugin.ShowVerb)
        {
            SelectedRecipe = Recipes.FirstOrDefault(r => r.Id == handoff.Note);
            return;
        }

        var before = Recipes.Count;
        foreach (var path in handoff.Paths)
            AddFromFile(path);
        var added = Recipes.Count - before;
        handoff.Answer(_host.Text.Format("pantry.reply.added", added));
    }

    /// <summary>
    /// Something changed. Saving on every keystroke is cheap here, and the alternative is a Save
    /// button that eventually gets left unpressed.
    /// </summary>
    internal void Touched()
    {
        Save();
        OnPropertyChanged(nameof(Summary));
        Recheck();
    }

    /// <summary>Reads the calendar again without touching what is stored.</summary>
    public void Refresh()
    {
        Cookbook.RollPlan(_settings.Plan, DateTime.Today);
        Save();
        RebuildAll();
        Status = _host.Text.Format("pantry.status.refreshed", DateTime.Now.ToString("HH:mm"));
    }

    private void RebuildAll()
    {
        var recipeId = SelectedRecipe?.Id;
        var stockId = SelectedStock?.Id;
        var day = SelectedDay?.Date;

        Recipes.Clear();
        foreach (var recipe in _settings.Recipes.OrderBy(r => r.Title, StringComparer.CurrentCultureIgnoreCase))
            Recipes.Add(new RecipeRowViewModel(recipe, this));
        SelectedRecipe = Recipes.FirstOrDefault(r => r.Id == recipeId);

        Stock.Clear();
        foreach (var item in _settings.Stock.OrderBy(s => s.Expires).ThenBy(s => s.Name, StringComparer.CurrentCultureIgnoreCase))
            Stock.Add(new StockRowViewModel(item, this));
        SelectedStock = Stock.FirstOrDefault(s => s.Id == stockId);

        PlanOptions.Clear();
        PlanOptions.Add(new RecipeChoice(null, _host.Text["pantry.plan.empty"]));
        foreach (var recipe in Recipes)
            PlanOptions.Add(new RecipeChoice(recipe.Id, recipe.Shown));

        Plan.Clear();
        foreach (var slot in _settings.Plan.OrderBy(s => s.Date))
            Plan.Add(new PlanRowViewModel(slot, this));
        SelectedDay = Plan.FirstOrDefault(p => p.Date == (day ?? DateTime.Today)) ?? Plan.FirstOrDefault();

        OnPropertyChanged(nameof(Summary));
        Recheck();
        RaiseCommandStates();
    }

    /// <summary>
    /// What ran out is said out loud from wherever the user happens to be, and stays said until
    /// it is used up.
    ///
    /// One key, so a re-check replaces the entry rather than stacking another one on top, and
    /// both branches are here: a condition set and never cleared is worse than no condition.
    /// </summary>
    private void Recheck()
    {
        var today = DateTime.Today;
        var expired = Cookbook.Expired(_settings.Stock, today);
        var soon = Cookbook.Soon(_settings.Stock, today);

        if (expired.Count == 0 && soon.Count == 0)
        {
            _host.Notifications.ClearCondition(StockKey);
            return;
        }

        var worst = expired.Count > 0 ? expired : soon;
        var severity = expired.Count > 0 ? NotificationSeverity.Warning : NotificationSeverity.Info;

        var title = expired.Count > 0
            ? _host.Text.Format("pantry.notify.expired", expired.Count)
            : _host.Text.Format("pantry.notify.soon", soon.Count);
        var named = string.Join(", ", worst.Take(3).Select(s =>
            s.Name.Trim().Length > 0 ? s.Name.Trim() : _host.Text["pantry.untitled"]));
        var message = worst.Count > 3
            ? _host.Text.Format("pantry.notify.more", named, worst.Count - 3)
            : named;

        _host.Notifications.SetCondition(StockKey, severity, title, message,
            new NotificationAction(_host.Text["pantry.notify.recheck"], Refresh));
    }

    private void RaiseCommandStates()
    {
        DeleteRecipeCommand.RaiseCanExecuteChanged();
        UseUpCommand.RaiseCanExecuteChanged();
        CookCommand.RaiseCanExecuteChanged();
        ClearDayCommand.RaiseCanExecuteChanged();
        AddStockCommand.RaiseCanExecuteChanged();
    }

    private void Retranslate()
    {
        foreach (var recipe in Recipes)
            recipe.Reread();
        foreach (var item in Stock)
            item.Reread();
        foreach (var slot in Plan)
            slot.Reread();

        // Plan options carry names; rebuild them in the new language.
        var day = SelectedDay?.Date;
        PlanOptions.Clear();
        PlanOptions.Add(new RecipeChoice(null, _host.Text["pantry.plan.empty"]));
        foreach (var recipe in Recipes)
            PlanOptions.Add(new RecipeChoice(recipe.Id, recipe.Shown));
        foreach (var slot in Plan)
            slot.Reread();
        SelectedDay = Plan.FirstOrDefault(p => p.Date == (day ?? DateTime.Today)) ?? Plan.FirstOrDefault();

        // The standing condition was written in the old language and nobody would rewrite it, so
        // it is raised again in the new one.
        Recheck();

        OnEverythingChanged();
        OnPropertyChanged(nameof(Summary));
    }

    private void Save()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not save Pantry settings: {ex.Message}");
        }
    }

    /// <summary>Ctrl+K reaching into the box: by title, tag or a word from the recipe. Landing on one selects it.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        var hits = new List<SearchHit>();

        foreach (var recipe in Recipes)
        {
            if (hits.Count >= limit)
                break;
            if (!SearchWords.Match(words, [recipe.Shown, .. recipe.Recipe.Tags, recipe.Body]))
                continue;

            var chosen = recipe;
            hits.Add(new SearchHit(chosen.Shown, chosen.TimeText, () => SelectedRecipe = chosen));
        }

        return hits;
    }

    public void Dispose()
    {
        _watch?.Cancel();
        _language.Dispose();
    }

    // ---- importing, through the host's dialog rather than a TopLevel of our own ----

    private async Task ImportAsync()
    {
        try
        {
            var picked = await _host.Pick.File(new PickOptions
            {
                Title = _host.Text["pantry.dialog.recipe"],
                Filters = [PickFilter.Of("Markdown", "*.md", "*.txt")],
            });
            if (string.IsNullOrWhiteSpace(picked))
                return;
            AddFromFile(picked);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not pick: {ex.Message}");
        }
    }
}
