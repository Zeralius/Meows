using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia.Threading;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Basket.Services;

namespace Meows.Plugins.Basket.ViewModels;

public sealed class BasketSettings
{
    public List<BasketList> Lists { get; set; } = [];
}

public sealed class CheckItemViewModel : ObservableObject
{
    private readonly BasketViewModel _root;

    public CheckItemViewModel(BasketCheckItem item, BasketViewModel root)
    {
        Item = item;
        _root = root;
        RemoveCommand = new RelayCommand(() => _root.RemoveCheckItem(this));
    }

    public BasketCheckItem Item { get; }

    public string Id => Item.Id;

    public string Title
    {
        get => Item.Title;
        set
        {
            if (Item.Title == value)
                return;
            Item.Title = value ?? "";
            OnPropertyChanged();
            _root.Touched();
        }
    }

    public bool Done
    {
        get => Item.Done;
        set
        {
            if (Item.Done == value)
                return;
            Item.Done = value;
            OnPropertyChanged();
            _root.Touched();
        }
    }

    public RelayCommand RemoveCommand { get; }

    internal void Reread() => OnEverythingChanged();
}

public sealed class CardViewModel : ObservableObject
{
    private readonly BasketViewModel _root;

    public CardViewModel(BasketCard card, BasketViewModel root)
    {
        Card = card;
        _root = root;
    }

    public BasketCard Card { get; }

    public string Id => Card.Id;

    public string Title
    {
        get => Card.Title;
        set
        {
            if (Card.Title == value)
                return;
            Card.Title = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(Shown));
            _root.Touched();
        }
    }

    /// <summary>Something without a name yet still has to be findable in the list.</summary>
    public string Shown => Card.Title.Trim().Length > 0
        ? Card.Title
        : MeowsText.Current["basket.untitled"];

    public string Notes
    {
        get => Card.Notes;
        set
        {
            if (Card.Notes == value)
                return;
            Card.Notes = value ?? "";
            OnPropertyChanged();
            _root.Touched();
        }
    }

    /// <summary>
    /// The date as the picker wants it. Avalonia hands out a <see cref="DateTimeOffset"/>; only
    /// the day is kept, because an offset would put a date into a different day depending on
    /// where the machine thinks it is.
    /// </summary>
    public DateTimeOffset? DueOn
    {
        get => Card.Due is { } due
            ? new DateTimeOffset(DateTime.SpecifyKind(due.Date, DateTimeKind.Unspecified), TimeSpan.Zero)
            : null;
        set
        {
            DateTime? date = value?.Date;
            if (Card.Due?.Date == date?.Date)
                return;

            Card.Due = date?.Date;
            OnPropertyChanged();
            Reread();
            _root.Touched();
        }
    }

    public bool HasDue => Card.Due.HasValue;

    public bool Done
    {
        get => Card.Done;
        set
        {
            if (Card.Done == value)
                return;
            Card.Done = value;
            OnPropertyChanged();
            Reread();
            _root.CardDoneChanged(this);
        }
    }

    public bool IsOverdue => !Card.Done && Card.Due?.Date < DateTime.Today;

    public bool IsDueToday => !Card.Done && Card.Due?.Date == DateTime.Today;

    public string DueText => Board.DueText(Card, DateTime.Today, MeowsText.Current, BasketViewModel.Culture);

    public string? LinkPath => Card.LinkPath;

    public bool HasLink => !string.IsNullOrWhiteSpace(Card.LinkPath);

    public string LinkName => Card.LinkPath is { } path ? Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)) : "";

    public ObservableCollection<CheckItemViewModel> Checks { get; } = [];

    public bool HasChecks => Checks.Count > 0;

    public string CheckSummary => Checks.Count == 0
        ? ""
        : $"{Checks.Count(c => c.Done)}/{Checks.Count}";

    /// <summary>Everything on the row reads differently after a date change or a language change.</summary>
    internal void Reread() => OnEverythingChanged();
}

public sealed class ListViewModel : ObservableObject
{
    private readonly BasketViewModel _root;

    public ListViewModel(BasketList list, BasketViewModel root)
    {
        List = list;
        _root = root;
    }

    public BasketList List { get; }

    public string Id => List.Id;

    public string Title
    {
        get => List.Title;
        set
        {
            if (List.Title == value)
                return;
            List.Title = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(Shown));
            _root.Touched();
        }
    }

    public string Shown => List.Title.Trim().Length > 0
        ? List.Title
        : MeowsText.Current["basket.untitled"];

    public ObservableCollection<CardViewModel> Cards { get; } = [];

    public int Count => Cards.Count;

    internal void Reread() => OnEverythingChanged();
}

public sealed class BasketViewModel : ObservableObject, IDisposable, ISearchable, IHandoffTarget, IGlanceable, IMultiGlance, IActionTarget
{
    /// <summary>The condition key. One per plugin scope, so it replaces rather than stacks.</summary>
    private const string DueKey = "due";

    private readonly IMeowsHost _host;
    private readonly BasketSettings _settings;
    private readonly LanguageWatch _language;
    private readonly IBackgroundTask? _watch;

    private ListViewModel? _selectedList;
    private CardViewModel? _selectedCard;
    private string? _status;
    private string? _errorMessage;
    private string _newCheckTitle = "";

    public BasketViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<BasketSettings>() ?? new BasketSettings();
        Board.EnsureDefaults(_settings.Lists);
        Save();

        AddListCommand = new RelayCommand(AddList);
        DeleteListCommand = new RelayCommand(DeleteList, () => SelectedList is not null);
        AddCardCommand = new RelayCommand(AddCard, () => SelectedList is not null);
        DeleteCardCommand = new RelayCommand(DeleteCard, () => SelectedCard is not null);
        FinishCommand = new RelayCommand(ToggleDone, () => SelectedCard is not null);
        MoveLeftCommand = new RelayCommand(() => Move(-1), () => CanMove(-1));
        MoveRightCommand = new RelayCommand(() => Move(1), () => CanMove(1));
        SetTodayCommand = new RelayCommand(SetDueToday, () => SelectedCard is not null);
        ClearDueCommand = new RelayCommand(ClearDue, () => SelectedCard?.HasDue == true);
        OpenLinkCommand = new RelayCommand(() => Open(SelectedCard?.LinkPath), () => SelectedCard?.HasLink == true);
        UnlinkCommand = new RelayCommand(Unlink, () => SelectedCard?.HasLink == true);
        AddCheckCommand = new RelayCommand(AddCheck, () => SelectedCard is not null && !string.IsNullOrWhiteSpace(NewCheckTitle));
        RefreshCommand = new RelayCommand(Refresh);

        Rebuild();

        // Nothing here is expensive; the point of the timer is the calendar. A tab left open
        // across midnight would otherwise still be showing yesterday's arithmetic and would
        // never raise the thing that fell due while it sat there.
        _watch = host.Background.Schedule(
            host.Text["basket.task.watch"], TimeSpan.FromHours(6),
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

    public ObservableCollection<ListViewModel> Lists { get; } = [];

    public RelayCommand AddListCommand { get; }

    public RelayCommand DeleteListCommand { get; }

    public RelayCommand AddCardCommand { get; }

    public RelayCommand DeleteCardCommand { get; }

    public RelayCommand FinishCommand { get; }

    public RelayCommand MoveLeftCommand { get; }

    public RelayCommand MoveRightCommand { get; }

    public RelayCommand SetTodayCommand { get; }

    public RelayCommand ClearDueCommand { get; }

    public RelayCommand OpenLinkCommand { get; }

    public RelayCommand UnlinkCommand { get; }

    public RelayCommand AddCheckCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public ListViewModel? SelectedList
    {
        get => _selectedList;
        set
        {
            if (!SetField(ref _selectedList, value))
                return;

            if (value is not null && SelectedCard is not null && !value.Cards.Contains(SelectedCard))
                SelectedCard = null;

            OnPropertyChanged(nameof(HasList));
            OnPropertyChanged(nameof(HasCards));
            RaiseCommandStates();
        }
    }

    public CardViewModel? SelectedCard
    {
        get => _selectedCard;
        set
        {
            if (!SetField(ref _selectedCard, value))
                return;

            OnPropertyChanged(nameof(HasCard));
            OnPropertyChanged(nameof(SelectedMoveTarget));
            RaiseCommandStates();
        }
    }

    /// <summary>The list the selected card lives in, as a dropdown row. Picking another one moves the card there.</summary>
    public ListViewModel? SelectedMoveTarget
    {
        get => SelectedCard is null ? null : Lists.FirstOrDefault(l => l.Cards.Any(c => c.Id == SelectedCard.Id));
        set
        {
            if (value is null || SelectedCard is null)
                return;
            MoveCardTo(SelectedCard, value);
        }
    }

    public string NewCheckTitle
    {
        get => _newCheckTitle;
        set
        {
            if (SetField(ref _newCheckTitle, value))
                AddCheckCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasList => SelectedList is not null;

    public bool HasCard => SelectedCard is not null;

    public bool HasCards => Lists.Any(l => l.Cards.Count > 0);

    public string Status
    {
        get => _status ?? _host.Text["basket.status.ready"];
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

    /// <summary>The line under the header: what is late, what is coming up, and nothing else.</summary>
    public string Summary => Board.SummaryOf(_settings.Lists, DateTime.Today, _host.Text);

    /// <summary>The same line on the Home tab, red while something is late.</summary>
    public Glance? Glance() => Board.GlanceOf(_settings.Lists, DateTime.Today, _host.Text);

    /// <summary>The summary plus the most overdue cards, most overdue first. Home shows three at most.</summary>
    public IReadOnlyList<Glance> Glances()
    {
        var lines = new List<Glance>();
        if (Glance() is { } summary)
            lines.Add(summary);
        else
            return [];
        lines.AddRange(Board.Overdue(_settings.Lists, DateTime.Today)
            .Take(2)
            .Select(c => new Glance(
                c.Card.Title.Trim().Length > 0 ? c.Card.Title.Trim() : _host.Text["basket.untitled"],
                IsTrouble: true)));
        return lines;
    }

    /// <summary>Adds a list and selects it, because the next thing wanted is to name it.</summary>
    public void AddList()
    {
        var list = new BasketList { Title = _host.Text["basket.list.new"] };
        _settings.Lists.Add(list);
        Save();
        Rebuild();
        SelectedList = Lists.FirstOrDefault(l => l.Id == list.Id);
        Status = _host.Text.Format("basket.status.list.added", list.Title);
    }

    private void DeleteList()
    {
        if (SelectedList is not { } selected)
            return;

        // No confirmation, deliberately. These are titles in a settings file, not files on disk,
        // and a wrongly deleted list costs a retype rather than the thing itself.
        var name = selected.Shown;
        _settings.Lists.RemoveAll(l => l.Id == selected.Id);
        Save();
        Rebuild();
        Status = _host.Text.Format("basket.status.deleted", name);
    }

    /// <summary>Adds a card to the selected list and selects it, because the next thing wanted is to name it.</summary>
    public void AddCard()
    {
        var list = SelectedList?.List ?? _settings.Lists.FirstOrDefault();
        if (list is null)
        {
            AddList();
            list = SelectedList?.List;
            if (list is null)
                return;
        }

        var card = new BasketCard { Title = _host.Text["basket.card.new"] };
        list.Cards.Add(card);
        _host.Store.Record("added", card.Title, list.Title);
        Save();
        Rebuild();
        SelectedCard = Lists.SelectMany(l => l.Cards).FirstOrDefault(c => c.Id == card.Id);
        Status = _host.Text.Format("basket.status.added", card.Title);
    }

    private void DeleteCard()
    {
        if (SelectedCard is not { } selected)
            return;

        var name = selected.Shown;
        foreach (var list in _settings.Lists)
            list.Cards.RemoveAll(c => c.Id == selected.Id);
        Save();
        SelectedCard = null;
        Rebuild();
        Status = _host.Text.Format("basket.status.deleted", name);
    }

    private void ToggleDone()
    {
        if (SelectedCard is not null)
            SelectedCard.Done = !SelectedCard.Done;
    }

    /// <summary>A tick in the row or the detail pane: finished, journaled, or open again.</summary>
    internal void CardDoneChanged(CardViewModel card)
    {
        if (card.Done)
        {
            _host.Store.Record("finished", card.Shown);
            Status = _host.Text.Format("basket.status.done", card.Shown);
        }
        else
        {
            Status = _host.Text.Format("basket.status.reopened", card.Shown);
        }

        Save();
        OnPropertyChanged(nameof(Summary));
        Recheck();
    }

    private bool CanMove(int direction)
    {
        if (SelectedCard is null)
            return false;
        var index = _settings.Lists.FindIndex(l => l.Cards.Any(c => c.Id == SelectedCard.Id));
        return index >= 0 && index + direction >= 0 && index + direction < _settings.Lists.Count;
    }

    private void Move(int direction)
    {
        if (SelectedCard is null)
            return;
        var index = _settings.Lists.FindIndex(l => l.Cards.Any(c => c.Id == SelectedCard.Id));
        if (index < 0 || index + direction < 0 || index + direction >= _settings.Lists.Count)
            return;

        MoveCardTo(SelectedCard, _settings.Lists[index + direction]);
    }

    private void MoveCardTo(CardViewModel card, ListViewModel target)
    {
        var found = Board.FindCard(_settings.Lists, card.Id);
        var destination = _settings.Lists.FirstOrDefault(l => l.Id == target.Id);
        if (found is not { } located || destination is null || located.List.Id == destination.Id)
            return;

        located.List.Cards.RemoveAll(c => c.Id == card.Id);
        destination.Cards.Add(located.Card);
        _host.Store.Record("moved", card.Shown, $"{located.List.Title} → {destination.Title}");
        Save();
        Rebuild();
        Status = _host.Text.Format("basket.status.moved", card.Shown, destination.Title);
    }

    private void MoveCardTo(CardViewModel card, BasketList destination)
    {
        var found = Board.FindCard(_settings.Lists, card.Id);
        if (found is not { } located || located.List.Id == destination.Id)
            return;

        located.List.Cards.RemoveAll(c => c.Id == card.Id);
        destination.Cards.Add(located.Card);
        _host.Store.Record("moved", card.Shown, $"{located.List.Title} → {destination.Title}");
        Save();
        Rebuild();
        Status = _host.Text.Format("basket.status.moved", card.Shown, destination.Title);
    }

    private void SetDueToday()
    {
        if (SelectedCard is not null)
            SelectedCard.DueOn = new DateTimeOffset(DateTime.SpecifyKind(DateTime.Today, DateTimeKind.Unspecified), TimeSpan.Zero);
    }

    private void ClearDue()
    {
        if (SelectedCard is not null)
            SelectedCard.DueOn = null;
    }

    private void AddCheck()
    {
        if (SelectedCard is not { } selected || string.IsNullOrWhiteSpace(NewCheckTitle))
            return;

        selected.Card.Checklist.Add(new BasketCheckItem { Title = NewCheckTitle.Trim() });
        NewCheckTitle = "";
        Save();
        selected.Reread();
        RebuildChecks(selected);
    }

    internal void RemoveCheckItem(CheckItemViewModel item)
    {
        foreach (var list in Lists)
            foreach (var card in list.Cards)
                if (card.Card.Checklist.RemoveAll(c => c.Id == item.Id) > 0)
                {
                    Save();
                    card.Reread();
                    RebuildChecks(card);
                    return;
                }
    }

    private void RebuildChecks(CardViewModel card)
    {
        card.Checks.Clear();
        foreach (var check in card.Card.Checklist)
            card.Checks.Add(new CheckItemViewModel(check, this));
        card.Reread();
    }

    /// <summary>A file or folder dropped on the tab becomes most of a card: titled, linked, in this list.</summary>
    public void AddFromPath(string path)
    {
        var list = SelectedList?.List ?? _settings.Lists.FirstOrDefault();
        if (list is null)
        {
            Board.EnsureDefaults(_settings.Lists);
            list = _settings.Lists.First();
        }

        var card = new BasketCard { Title = Board.TitleFromPath(path), LinkPath = path };
        list.Cards.Add(card);
        _host.Store.Record("added", card.Title, list.Title);
        Save();
        Rebuild();
        SelectedCard = Lists.SelectMany(l => l.Cards).FirstOrDefault(c => c.Id == card.Id);
        Status = _host.Text.Format("basket.status.added", card.Title);
    }

    public bool Accepts(Handoff handoff)
    {
        if (handoff.Verb == BasketPlugin.ShowVerb && handoff.Note is { Length: > 0 })
            return true;

        if ((handoff.Verb == HandoffVerbs.Files || handoff.Verb == HandoffVerbs.Folder) && handoff.Paths.Count > 0)
            return handoff.Paths.All(p => File.Exists(p) || Directory.Exists(p));

        return false;
    }

    public void Receive(Handoff handoff)
    {
        if (!Accepts(handoff))
            return;

        // From Ctrl+K while Basket was off: the hit, now that there is a board to select in.
        if (handoff.Verb == BasketPlugin.ShowVerb)
        {
            var found = Board.FindCard(_settings.Lists, handoff.Note ?? "");
            if (found is { } located)
            {
                SelectedList = Lists.FirstOrDefault(l => l.Id == located.List.Id);
                SelectedCard = SelectedList?.Cards.FirstOrDefault(c => c.Id == located.Card.Id);
            }
            return;
        }

        var before = Board.AllCards(_settings.Lists).Count();
        foreach (var path in handoff.Paths)
            AddFromPath(path);
        var added = Board.AllCards(_settings.Lists).Count() - before;
        handoff.Answer(_host.Text.Format("basket.reply.added", added));
    }

    /// <summary>
    /// A rule's "add a card": named after what the rule was about, in the first list, with the
    /// other plugin's words as its note and the file linked when there is one. Nothing is
    /// selected, since nobody is necessarily looking. Asking twice for the same thing is one
    /// card, not two.
    /// </summary>
    public Task<string> Perform(ActionRequest request, CancellationToken token)
    {
        if (request.Action != BasketPlugin.AddAction)
            throw new ActionDeclinedException(_host.Text.Format("basket.action.unknown", request.Action));

        var path = request.Path;
        var linked = File.Exists(path) || Directory.Exists(path) ? path : null;
        var title = linked is not null ? Board.TitleFromPath(path) : request.Subject.Trim();
        if (title.Length == 0)
            title = _host.Text["basket.untitled"];

        if (Board.AllCards(_settings.Lists).Any(c => !c.Card.Done && string.Equals(c.Card.Title, title, StringComparison.CurrentCultureIgnoreCase)))
            return Task.FromResult(_host.Text.Format("basket.action.already", title));

        if (_settings.Lists.Count == 0)
            Board.EnsureDefaults(_settings.Lists);

        _settings.Lists[0].Cards.Add(new BasketCard
        {
            Title = title,
            Notes = request.Cause.Detail ?? "",
            LinkPath = linked,
        });
        Save();
        Rebuild();

        return Task.FromResult(_host.Text.Format("basket.action.added", title));
    }

    /// <summary>
    /// Something on a card changed. Saving on every keystroke is cheap here, and the alternative
    /// is a Save button that eventually gets left unpressed.
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
        foreach (var list in Lists)
            foreach (var card in list.Cards)
            {
                card.Reread();
                RebuildChecks(card);
            }

        Recheck();
        OnPropertyChanged(nameof(Summary));
        Status = _host.Text.Format("basket.status.refreshed", DateTime.Now.ToString("HH:mm"));
    }

    private void Rebuild()
    {
        var listId = SelectedList?.Id;
        var cardId = SelectedCard?.Id;

        Lists.Clear();
        foreach (var list in _settings.Lists)
        {
            var view = new ListViewModel(list, this);
            foreach (var stored in list.Cards)
            {
                var cardView = new CardViewModel(stored, this);
                foreach (var check in stored.Checklist)
                    cardView.Checks.Add(new CheckItemViewModel(check, this));
                view.Cards.Add(cardView);
            }
            Lists.Add(view);
        }

        SelectedList = Lists.FirstOrDefault(l => l.Id == listId) ?? Lists.FirstOrDefault();

        CardViewModel? card = null;
        if (cardId is not null)
            card = Lists.SelectMany(l => l.Cards).FirstOrDefault(c => c.Id == cardId);
        if (card is not null)
        {
            var owner = Lists.First(l => l.Cards.Contains(card));
            if (SelectedList?.Id != owner.Id)
                SelectedList = owner;
            SelectedCard = card;
        }
        else
        {
            SelectedCard = null;
        }

        OnPropertyChanged(nameof(HasCards));
        OnPropertyChanged(nameof(Summary));
        Recheck();
        RaiseCommandStates();
    }

    /// <summary>
    /// The whole point of watching dates: something due is said out loud from wherever the user
    /// happens to be, and stays said until it is dealt with.
    ///
    /// One key, so a re-check replaces the entry rather than stacking another one on top, and
    /// both branches are here: a condition set and never cleared is worse than no condition.
    /// </summary>
    private void Recheck()
    {
        var today = DateTime.Today;
        var overdue = Board.Overdue(_settings.Lists, today);
        var soon = Board.DueSoon(_settings.Lists, today);

        if (overdue.Count == 0 && soon.Count == 0)
        {
            _host.Notifications.ClearCondition(DueKey);
            return;
        }

        var worst = overdue.Count > 0 ? overdue : soon;
        var severity = overdue.Count > 0 ? NotificationSeverity.Warning : NotificationSeverity.Info;

        var title = _host.Text.Format("basket.notify.due", overdue.Count + soon.Count);
        var named = string.Join(", ", worst.Take(3).Select(c =>
            c.Card.Title.Trim().Length > 0 ? c.Card.Title.Trim() : _host.Text["basket.untitled"]));
        var message = worst.Count > 3
            ? _host.Text.Format("basket.notify.more", named, worst.Count - 3)
            : named;

        _host.Notifications.SetCondition(DueKey, severity, title, message,
            new NotificationAction(_host.Text["basket.notify.recheck"], Refresh));
    }

    private void Retranslate()
    {
        foreach (var list in Lists)
        {
            list.Reread();
            foreach (var card in list.Cards)
            {
                card.Reread();
                foreach (var check in card.Checks)
                    check.Reread();
            }
        }

        // The standing condition was written in the old language and nobody would rewrite it, so
        // it is raised again in the new one.
        Recheck();

        OnEverythingChanged();
    }

    private void RaiseCommandStates()
    {
        DeleteListCommand.RaiseCanExecuteChanged();
        AddCardCommand.RaiseCanExecuteChanged();
        DeleteCardCommand.RaiseCanExecuteChanged();
        FinishCommand.RaiseCanExecuteChanged();
        MoveLeftCommand.RaiseCanExecuteChanged();
        MoveRightCommand.RaiseCanExecuteChanged();
        SetTodayCommand.RaiseCanExecuteChanged();
        ClearDueCommand.RaiseCanExecuteChanged();
        OpenLinkCommand.RaiseCanExecuteChanged();
        UnlinkCommand.RaiseCanExecuteChanged();
        AddCheckCommand.RaiseCanExecuteChanged();
    }

    private void Unlink()
    {
        if (SelectedCard is null)
            return;

        SelectedCard.Card.LinkPath = null;
        Save();
        SelectedCard.Reread();
        RaiseCommandStates();
    }

    private void Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        if (!File.Exists(path) && !Directory.Exists(path))
        {
            ErrorMessage = _host.Text.Format("basket.error.gone", path);
            return;
        }

        ErrorMessage = null;

        try
        {
            Explorer.Open(path);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("basket.error.open", path, ex.Message);
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
            _host.Log(LogLevel.Warning, $"Could not save Basket settings: {ex.Message}");
        }
    }

    /// <summary>Ctrl+K reaching into the cards: by title, note, list or the file linked. Landing on one selects it.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        var hits = new List<SearchHit>();

        foreach (var list in Lists)
            foreach (var card in list.Cards)
            {
                if (hits.Count >= limit)
                    return hits;

                var file = card.LinkPath is { } path ? Path.GetFileName(path) : null;
                if (!SearchWords.Match(words, card.Shown, card.Notes, list.Shown, file))
                    continue;

                var chosenList = list;
                var chosen = card;
                hits.Add(new SearchHit(chosen.Shown, $"{chosenList.Shown} · {chosen.DueText}", () =>
                {
                    SelectedList = chosenList;
                    SelectedCard = chosen;
                }));
            }

        return hits;
    }

    public void Dispose()
    {
        _watch?.Cancel();
        _language.Dispose();
    }

    // ---- picking and exporting, through the host's dialogs rather than a TopLevel of our own ----

    private RelayCommand? _attachCommand;

    public RelayCommand AttachCommand => _attachCommand ??= new RelayCommand(() => _ = AttachAsync());

    private async Task AttachAsync()
    {
        if (SelectedCard is null)
            return;

        try
        {
            var picked = await _host.Pick.File(new PickOptions { Title = _host.Text["basket.dialog.link"] });
            if (string.IsNullOrWhiteSpace(picked))
                return;

            SelectedCard.Card.LinkPath = picked;
            Save();
            SelectedCard.Reread();
            RaiseCommandStates();
            Status = _host.Text.Format("basket.status.attached", Path.GetFileName(picked));
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not pick: {ex.Message}");
        }
    }

    private RelayCommand? _exportCommand;

    public RelayCommand ExportCommand => _exportCommand ??= new RelayCommand(() => _ = ExportAsync());

    public async Task ExportAsync()
    {
        try
        {
            var picked = await _host.Pick.Save(new PickOptions
            {
                Title = _host.Text["basket.dialog.export"],
                SuggestedName = $"basket-{DateTime.Today:yyyy-MM-dd}.md",
            });
            if (string.IsNullOrWhiteSpace(picked))
                return;

            var total = Board.AllCards(_settings.Lists).Count();
            File.WriteAllText(picked, Board.ExportMarkdown(_settings.Lists, DateTime.Today));
            Status = _host.Text.Format("basket.status.exported", total, Path.GetFileName(picked));
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("basket.error.open", _host.Text["basket.dialog.export"], ex.Message);
        }
    }
}
