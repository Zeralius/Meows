using System.Collections.ObjectModel;
using System.Globalization;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Tin.Services;

namespace Meows.Plugins.Tin.ViewModels;

/// <summary>
/// A column mapping as it is stored. The service uses a positional record; this is the same three
/// numbers as a plain object, because a settings file outlives the shape of the type that wrote
/// it and a named field is the easier of the two to still read next year.
/// </summary>
public sealed class StoredMap
{
    public int Date { get; set; } = -1;

    public int Name { get; set; } = -1;

    public int Amount { get; set; } = -1;

    /// <summary>What the payment was for. Optional, so -1 is a fine answer.</summary>
    public int Reference { get; set; } = -1;
}

public sealed class TinSettings
{
    public string? Folder { get; set; }

    /// <summary>A CSV rarely says, so being told once beats guessing on every row.</summary>
    public string Currency { get; set; } = "EUR";

    /// <summary>Keyed by the header line, so one mapping covers every export from that bank.</summary>
    public Dictionary<string, StoredMap> Columns { get; set; } = new();

    /// <summary>Series the user has said are not subscriptions. Rent repeats too.</summary>
    public List<string> Ignored { get; set; } = [];

    /// <summary>What each account is called, keyed by IBAN. Nobody thinks in IBANs.</summary>
    public Dictionary<string, string> AccountNames { get; set; } = new();

    /// <summary>
    /// Files the user has assigned by hand, keyed by file name. Only needed when an export never
    /// names the account it is about, which is rarer than it sounds but does happen.
    /// </summary>
    public Dictionary<string, string> FileAccounts { get; set; } = new();

    /// <summary>The account being looked at, or empty for all of them.</summary>
    public string Account { get; set; } = "";

    /// <summary>Whether the panel on the right is open.</summary>
    public bool ShowChart { get; set; } = true;

    /// <summary>How wide it was dragged, in pixels.</summary>
    public double ChartPixels { get; set; } = 320;
}

/// <summary>
/// Numbers as the window's language writes them. A German export read on an English Windows
/// should still show German amounts when the window is in German, and the machine's own culture
/// has no say in either.
/// </summary>
public static class Shown
{
    public static CultureInfo Culture => MeowsText.Current.Language == "de"
        ? CultureInfo.GetCultureInfo("de-DE")
        : CultureInfo.GetCultureInfo("en-GB");

    public static string Money(decimal amount, string currency) =>
        Services.Money.Show(Math.Abs(amount), currency, Culture);

    public static string Date(DateTime date) => date.ToString("d", Culture);
}

public sealed class ChargeViewModel(Charge charge, string currency) : ObservableObject
{
    public string DateText => Shown.Date(charge.Date);

    public string Name => charge.Name;

    public string Reference => charge.Reference;

    public bool HasReference => charge.Reference.Length > 0;

    public string AmountText => Shown.Money(charge.Amount, currency);

    public string Source => charge.Source;

    internal void Reread() => OnEverythingChanged();
}

/// <summary>
/// A line in the list. Either one subscription, or one company that several subscriptions belong
/// to, which sits above them with their sum.
/// </summary>
public abstract class RowViewModel : ObservableObject
{
    public abstract string Key { get; }

    public abstract string Name { get; }

    /// <summary>Everything this row stands for, which is what gets put away or brought back.</summary>
    public abstract IEnumerable<string> Keys { get; }

    /// <summary>Everything this row stands for, as evidence for the pane underneath.</summary>
    public abstract IEnumerable<Charge> AllCharges { get; }

    public abstract string RiseText { get; }

    /// <summary>Indented under a company row rather than standing on its own.</summary>
    public bool IsChild { get; internal set; }

    internal abstract void Reread();
}

/// <summary>
/// One company that bills several things at once, standing above them with the total.
///
/// An energy company takes electricity and gas, an insurer takes three policies, and each of
/// those is its own subscription with its own amount and its own reference. But the question
/// asked of the list is usually what the company costs, and that is the sum, so the sum is the
/// row and the parts are indented under it.
/// </summary>
public sealed class FamilyViewModel(string key, string name, IReadOnlyList<SeriesViewModel> members, string currency)
    : RowViewModel
{
    public IReadOnlyList<SeriesViewModel> Members { get; } = members;

    public override string Key { get; } = key;

    public override string Name { get; } = name;

    public override IEnumerable<string> Keys => Members.Select(m => m.Key);

    public override IEnumerable<Charge> AllCharges => Members.SelectMany(m => m.Series.Charges);

    /// <summary>A rise is a fact about one contract, not about the company.</summary>
    public override string RiseText => "";

    /// <summary>What the company costs a month now, so a part that stopped is listed but not added.</summary>
    public decimal PerMonth => Members.Sum(m => m.Series.Ongoing);

    public string PerMonthText => MeowsText.Current.Format("tin.permonth",
        Services.Money.Show(PerMonth, currency, Shown.Culture));

    /// <summary>The parts as one figure, which is the number the row exists for.</summary>
    public string TotalText => Services.Money.Show(
        Members.Where(m => !m.Lapsed).Sum(m => Math.Abs(m.Series.LastAmount)), currency, Shown.Culture);

    public string CountText => MeowsText.Current.Format("tin.family.count", Members.Count);

    public bool WentUp => Members.Any(m => m.WentUp);

    public bool Lapsed => Members.All(m => m.Lapsed);

    internal override void Reread()
    {
        foreach (var member in Members)
            member.Reread();

        OnEverythingChanged();
    }
}

public sealed class SeriesViewModel(Series series, string currency) : RowViewModel
{
    public Series Series { get; } = series;

    public override string Key => Series.Key;

    public override string Name => Series.Name;

    public override IEnumerable<string> Keys => [Series.Key];

    public override IEnumerable<Charge> AllCharges => Series.Charges;

    /// <summary>
    /// What the payment says about itself: the customer number, the contract, the period. Shown
    /// under the name because it is what tells three bills from the same payee apart, which the
    /// name on its own cannot.
    /// </summary>
    public string Reference => Series.Reference;

    public bool HasReference => Series.Reference.Length > 0;

    public string CadenceText => MeowsText.Current[Recurring.Describe(Series.Cadence)];

    public string AmountText => Shown.Money(Series.LastAmount, currency);

    public string PerMonthText => MeowsText.Current.Format("tin.permonth",
        Services.Money.Show(Series.PerMonth, currency, Shown.Culture));

    public string SeenText => MeowsText.Current.Format("tin.seen", Series.Charges.Count,
        Shown.Date(Series.Last));

    public string DueText => Series.Lapsed
        ? MeowsText.Current.Format("tin.lapsed", Shown.Date(Series.Due))
        : MeowsText.Current.Format("tin.due", Shown.Date(Series.Due));

    public bool WentUp => Series.WentUp;

    public bool Lapsed => Series.Lapsed;

    public override string RiseText => Series.PreviousAmount is { } previous
        ? MeowsText.Current.Format("tin.rise",
            Services.Money.Show(Series.Rise, currency, Shown.Culture),
            Services.Money.Show(Math.Abs(previous), currency, Shown.Culture))
        : "";

    internal override void Reread() => OnEverythingChanged();
}

/// <summary>One export file, and whether it was understood.</summary>
public sealed class SourceViewModel(SourceFile file) : ObservableObject
{
    public SourceFile File { get; } = file;

    public string FileName => File.FileName;

    public bool IsUnderstood => File.IsUnderstood;

    public string Detail => File.Problem is { } problem
        ? MeowsText.Current[problem]
        : MeowsText.Current.Format("tin.source.read", File.Charges, File.Rows);

    internal void Reread() => OnEverythingChanged();
}

/// <summary>
/// One account the folder holds statements for.
///
/// A folder is very often one account, and just as often two, because the statements of a second
/// account were downloaded into the same place. Keeping them apart matters for more than tidiness:
/// the same standing order leaving two accounts on the same day is two payments, and a monthly
/// total that adds two people's accounts together is not a number anybody wanted.
/// </summary>
public sealed class AccountViewModel(string key, string? named, int files, int charges, decimal monthly, string currency)
    : ObservableObject
{
    private bool _isOn;

    public string Key { get; } = key;

    public bool IsEverything => Key.Length == 0;

    public string Name => named is { Length: > 0 }
        ? named
        : Key switch
        {
            "" => MeowsText.Current["tin.account.all"],
            Iban.Unknown => MeowsText.Current["tin.account.unknown"],
            _ => Iban.Mask(Key),
        };

    /// <summary>The IBAN under a name the user gave it, so the name never hides which one it is.</summary>
    public string Under => named is { Length: > 0 } && Key.Length > 0 && Key != Iban.Unknown
        ? Iban.Mask(Key)
        : "";

    public bool HasUnder => Under.Length > 0;

    public string Detail => MeowsText.Current.Format("tin.account.detail", files, charges,
        Money.Show(monthly, currency, Shown.Culture));

    public bool IsOn
    {
        get => _isOn;
        set => SetField(ref _isOn, value);
    }

    internal void Reread() => OnEverythingChanged();
}

/// <summary>What a file can be told it belongs to.</summary>
public sealed class AccountChoice(string key, string label) : ObservableObject
{
    public string Key { get; } = key;

    private readonly string _label = label;

    public string Name => Key.Length == 0 ? MeowsText.Current["tin.account.auto"] : _label;

    internal void Reread() => OnPropertyChanged(nameof(Name));
}

/// <summary>
/// One wedge of the ring, and its row in the legend beside it.
/// </summary>
public sealed class SliceViewModel(Slice slice, int index, string currency) : ObservableObject
{
    /// <summary>
    /// Colours for data rather than for chrome, which is why they are here and not in the shell's
    /// palette. A wedge has to be told apart from the wedge beside it, so these are chosen to stay
    /// distinct from one another and legible on both a light and a dark ground, rather than to
    /// follow the theme. The last one is the grey that everything past the eighth falls into.
    /// </summary>
    private static readonly uint[] Wheel =
    [
        0xFFE69F00, 0xFF56B4E9, 0xFF009E73, 0xFF0072B2,
        0xFFD55E00, 0xFFCC79A7, 0xFF8C6BB1, 0xFF66A61E,
    ];

    private const uint Rest = 0xFF9A9A9A;

    public Slice Slice { get; } = slice;

    public string Name => Slice.IsRest ? MeowsText.Current["tin.chart.rest"] : Slice.Name;

    public double Share => Slice.Share;

    public string ShareText => (Slice.Share * 100).ToString("N1", Shown.Culture) + " %";

    public string TotalText => Money.Show(Slice.Total, currency, Shown.Culture);

    public IBrush Fill { get; } = new SolidColorBrush(
        Color.FromUInt32(slice.IsRest ? Rest : Wheel[index % Wheel.Length]));

    internal void Reread() => OnPropertyChanged(nameof(Name));
}

/// <summary>One filter button, with what it would show.</summary>
public sealed class BucketViewModel(string nameKey, string key, int count, string detail) : ObservableObject
{
    private bool _isOn;

    public string Name => MeowsText.Current[nameKey];

    public string Key { get; } = key;

    public int Count { get; } = count;

    public string Detail { get; } = detail;

    public bool IsOn
    {
        get => _isOn;
        set => SetField(ref _isOn, value);
    }

    internal void Reread() => OnEverythingChanged();
}

public sealed class TinViewModel : ObservableObject, IDisposable, ISearchable
{
    public const string BucketAll = "all";
    public const string BucketUp = "up";
    public const string BucketStopped = "stopped";
    public const string BucketHidden = "hidden";

    private readonly IMeowsHost _host;
    private readonly TinSettings _settings;
    private readonly LanguageWatch _language;

    private Reading _reading = Reading.Empty;
    private string _account = "";
    private IReadOnlyList<Series> _series = [];
    private string _filter = BucketAll;
    private string? _status;
    private string? _errorMessage;
    private RowViewModel? _selected;
    private SourceViewModel? _selectedSource;

    public TinViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<TinSettings>() ?? new TinSettings();
        _account = _settings.Account;

        RefreshCommand = new RelayCommand(Refresh);
        FilterCommand = new RelayCommand(p => ApplyFilter((p as BucketViewModel)?.Key));
        AccountCommand = new RelayCommand(p => ShowAccount((p as AccountViewModel)?.Key ?? ""));
        OpenFolderCommand = new RelayCommand(() => Open(Folder), () => Directory.Exists(Folder));
        IgnoreCommand = new RelayCommand(Ignore, () => Selected is not null && !IsShowingHidden);
        RestoreCommand = new RelayCommand(Restore, () => Selected is not null && IsShowingHidden);
        ClearSourceCommand = new RelayCommand(() => SelectedSource = null, () => SelectedSource is not null);
        ChartCommand = new RelayCommand(() => ShowChart = !ShowChart);

        Refresh();

        _language = new LanguageWatch(Retranslate);
    }

    /// <summary>Every subscription shown, flat, whatever row it is drawn under.</summary>
    public ObservableCollection<SeriesViewModel> Series { get; } = [];

    /// <summary>What is actually drawn: company rows with their parts under them, and the rest.</summary>
    public ObservableCollection<RowViewModel> Rows { get; } = [];

    public ObservableCollection<ChargeViewModel> Charges { get; } = [];

    public ObservableCollection<BucketViewModel> Buckets { get; } = [];

    public ObservableCollection<SourceViewModel> Sources { get; } = [];

    public ObservableCollection<AccountViewModel> Accounts { get; } = [];

    public ObservableCollection<AccountChoice> AccountChoices { get; } = [];

    public ObservableCollection<SliceViewModel> Slices { get; } = [];

    public ObservableCollection<string> HeaderChoices { get; } = [];

    public RelayCommand RefreshCommand { get; }

    public RelayCommand FilterCommand { get; }

    public RelayCommand AccountCommand { get; }

    public RelayCommand OpenFolderCommand { get; }

    public RelayCommand IgnoreCommand { get; }

    public RelayCommand RestoreCommand { get; }

    public RelayCommand ClearSourceCommand { get; }

    public RelayCommand ChartCommand { get; }

    /// <summary>
    /// The panel on the right. Worth hiding: it is the answer to a question you ask now and then
    /// rather than every time you open the tab, and the list of subscriptions is the tab.
    /// </summary>
    public bool ShowChart
    {
        get => _settings.ShowChart;
        set
        {
            if (_settings.ShowChart == value)
                return;

            _settings.ShowChart = value;
            Save();
            OnPropertyChanged();
        }
    }

    /// <summary>
    /// The width of the panel, as the grid wants it.
    ///
    /// Written back when the splitter is dragged, which is why it is settable: Avalonia's splitter
    /// changes the column's own width, and a two way binding is what carries that back here to be
    /// remembered.
    /// </summary>
    public GridLength ChartWidth
    {
        get => new(Math.Clamp(_settings.ChartPixels, 240, 680), GridUnitType.Pixel);
        set
        {
            var pixels = value.IsAbsolute ? value.Value : 320;
            if (Math.Abs(_settings.ChartPixels - pixels) < 1)
                return;

            _settings.ChartPixels = pixels;
            Save();
            OnPropertyChanged();
        }
    }

    // ---- what a month usually costs, and what it usually brings in ----

    private Rhythm _out = new(0, 0, 0);
    private Rhythm _in = new(0, 0, 0);

    /// <summary>Enough whole months to say anything about a typical one.</summary>
    public bool HasOutlook => _out.IsUseful || _in.IsUseful;

    public bool HasNoOutlook => !HasOutlook && Slices.Count > 0;

    public string MonthsRead => _host.Text.Format("tin.outlook.months", Math.Max(_out.Months, _in.Months));

    public string OutNextMonth => Money.Show(_out.Total, Currency, Shown.Culture);

    public string InNextMonth => Money.Show(_in.Total, Currency, Shown.Culture);

    public string LeftNextMonth => Money.Show(_in.Total - _out.Total, Currency, Shown.Culture);

    public bool IsShortNextMonth => _in.Total < _out.Total;

    public string OutYear => Money.Show(_out.Total * 12m, Currency, Shown.Culture);

    public string InYear => Money.Show(_in.Total * 12m, Currency, Shown.Culture);

    public string LeftYear => Money.Show((_in.Total - _out.Total) * 12m, Currency, Shown.Culture);

    public bool IsShortYear => IsShortNextMonth;

    /// <summary>
    /// What the number is made of, said out loud. A forecast nobody can take apart is a forecast
    /// nobody should believe.
    /// </summary>
    public string OutMadeOf => _host.Text.Format("tin.outlook.madeof",
        Money.Show(_out.Known, Currency, Shown.Culture),
        Money.Show(_out.Typical, Currency, Shown.Culture));

    public string InMadeOf => _host.Text.Format("tin.outlook.madeof",
        Money.Show(_in.Known, Currency, Shown.Culture),
        Money.Show(_in.Typical, Currency, Shown.Culture));

    public string ChartSpan
    {
        get
        {
            var mine = Mine(_reading.Charges).ToList();
            return mine.Count == 0
                ? ""
                : _host.Text.Format("tin.chart.span",
                    Shown.Date(mine.Min(c => c.Date)), Shown.Date(mine.Max(c => c.Date)));
        }
    }

    public string Folder => _settings.Folder ?? "";

    public bool HasFolder => !string.IsNullOrWhiteSpace(_settings.Folder);

    public string Currency
    {
        get => _settings.Currency;
        set
        {
            var tidied = (value ?? "").Trim().ToUpperInvariant();
            if (tidied.Length == 0 || tidied == _settings.Currency)
                return;

            _settings.Currency = tidied;
            Save();
            OnPropertyChanged();

            // The bucket details carry money too, so they are rebuilt rather than left reading
            // in the currency that was there a moment ago.
            BuildBuckets();
            Rebuild();
        }
    }

    public string Status
    {
        get => _status ?? MeowsText.Current["tin.status.start"];
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

    public bool IsEmpty => Series.Count == 0;

    /// <summary>
    /// What the recurring charges add up to in a month, which is the number the whole tab exists
    /// to show. Hidden series are left out of it, because they were hidden for being something
    /// other than a subscription.
    /// </summary>
    public string MonthlyTotal
    {
        get
        {
            var total = Visible(BucketAll).Sum(s => s.Ongoing);
            return total == 0m
                ? ""
                : _host.Text.Format("tin.total", Services.Money.Show(total, Currency, Shown.Culture),
                    Services.Money.Show(total * 12m, Currency, Shown.Culture));
        }
    }

    /// <summary>Shown only when there is a choice to make. One account needs no chooser.</summary>
    public bool HasAccounts => Accounts.Count > 1;

    /// <summary>The name for the account being looked at, editable, empty for all of them.</summary>
    public string AccountName
    {
        get => _settings.AccountNames.GetValueOrDefault(_account, "");
        set
        {
            if (_account.Length == 0)
                return;

            var named = (value ?? "").Trim();

            if (named.Length == 0)
                _settings.AccountNames.Remove(_account);
            else
                _settings.AccountNames[_account] = named;

            Save();
            OnPropertyChanged();
            BuildAccounts();
        }
    }

    public bool IsShowingOneAccount => _account.Length > 0;

    public bool HasTotal => Visible(BucketAll).Any();

    /// <summary>
    /// Said out loud when a folder was read and produced nothing, with the reason. A tab that
    /// stays empty and says nothing reads as a broken plugin, and the commonest way to arrive
    /// here is a folder of statements the bank wrote as PDFs.
    /// </summary>
    public string? Advice
    {
        get
        {
            if (!HasFolder || HasError || _reading.Charges.Count > 0)
                return null;

            // A PDF that gave up nothing is the likeliest thing to go wrong here, and the two
            // ways it goes wrong want different answers.
            if (_reading.Sources.Any(s => s.Problem == PdfStatement.ProblemNothing))
                return _host.Text["tin.advice.pdfnothing"];

            if (_reading.Sources.Any(s => s.Problem == PdfStatement.ProblemSigns))
                return _host.Text["tin.advice.pdfsigns"];

            if (_reading.Sources.Any(s => s.Problem == Statement.ProblemUnreadable))
                return _host.Text["tin.advice.unreadable"];

            if (_reading.Sources.Any(s => s.Problem == Statement.ProblemNotData))
                return _host.Text["tin.advice.notdata"];

            return _reading.Sources.Count == 0 ? _host.Text["tin.advice.nofiles"] : null;
        }
    }

    public bool HasAdvice => Advice is not null;

    public string Summary => _reading.Sources.Count == 0
        ? ""
        : _host.Text.Format("tin.summary", _reading.Sources.Count, _reading.Charges.Count, _reading.Duplicates);

    public RowViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value))
                return;

            ShowCharges();
            OnPropertyChanged(nameof(HasSelection));
            IgnoreCommand.RaiseCanExecuteChanged();
            RestoreCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSelection => Selected is not null;

    public bool IsShowingHidden => _filter == BucketHidden;

    // ---- the column mapping, for when the guess was wrong ----

    public SourceViewModel? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!SetField(ref _selectedSource, value))
                return;

            HeaderChoices.Clear();
            if (value is not null)
            {
                foreach (var column in value.File.Header)
                    HeaderChoices.Add(column.Trim().Length == 0 ? MeowsText.Current["tin.column.unnamed"] : column.Trim());
            }

            OnPropertyChanged(nameof(HasSource));
            OnPropertyChanged(nameof(CanMap));
            OnPropertyChanged(nameof(DateColumn));
            OnPropertyChanged(nameof(NameColumn));
            OnPropertyChanged(nameof(AmountColumn));
            ClearSourceCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasSource => SelectedSource is not null;

    /// <summary>
    /// Which account the selected file belongs to.
    ///
    /// Nearly always worked out from the file itself, since every export names its own account
    /// somewhere. This is for the exports that do not, and for the ones that name it somewhere
    /// nothing has thought to look.
    /// </summary>
    public AccountChoice? SourceAccount
    {
        get
        {
            if (SelectedSource is not { } source)
                return null;

            var told = _settings.FileAccounts.GetValueOrDefault(source.File.FileName, "");
            return AccountChoices.FirstOrDefault(c => c.Key == told) ?? AccountChoices.FirstOrDefault();
        }

        set
        {
            if (SelectedSource is not { } source || value is null)
                return;

            var file = source.File.FileName;

            if (value.Key.Length == 0)
                _settings.FileAccounts.Remove(file);
            else
                _settings.FileAccounts[file] = value.Key;

            Save();
            OnPropertyChanged();
            Refresh();
            SelectedSource = Sources.FirstOrDefault(s => s.File.FileName == file);
        }
    }

    /// <summary>
    /// Columns are a thing a CSV has. A PDF has ink at coordinates, so there is nothing to point
    /// at and the pickers stay away rather than offering three empty dropdowns.
    /// </summary>
    public bool CanMap => SelectedSource is { } source && source.File.Header.Count > 0;

    public int DateColumn
    {
        get => SelectedSource?.File.Map.Date ?? -1;
        set => SetColumn(map => map.Date = value);
    }

    public int NameColumn
    {
        get => SelectedSource?.File.Map.Name ?? -1;
        set => SetColumn(map => map.Name = value);
    }

    public int AmountColumn
    {
        get => SelectedSource?.File.Map.Amount ?? -1;
        set => SetColumn(map => map.Amount = value);
    }

    /// <summary>
    /// Remembers the correction against the header line rather than the file name, so next
    /// month's export from the same bank is understood without being told again.
    /// </summary>
    private void SetColumn(Action<StoredMap> change)
    {
        if (SelectedSource is not { } source || source.File.Signature.Length == 0)
            return;

        if (!_settings.Columns.TryGetValue(source.File.Signature, out var map))
        {
            var guess = source.File.Map;
            map = new StoredMap
            {
                Date = guess.Date,
                Name = guess.Name,
                Amount = guess.Amount,
                Reference = guess.Reference,
            };
            _settings.Columns[source.File.Signature] = map;
        }

        change(map);
        Save();

        var signature = source.File.Signature;
        Refresh();

        // Refresh rebuilds the list, so the file that was being corrected has to be found again.
        SelectedSource = Sources.FirstOrDefault(s => s.File.Signature == signature);
    }

    public void SetFolder(string folder)
    {
        _settings.Folder = folder;
        Save();
        OnPropertyChanged(nameof(Folder));
        OnPropertyChanged(nameof(HasFolder));
        OpenFolderCommand.RaiseCanExecuteChanged();
        Refresh();
    }

    public void Refresh()
    {
        ErrorMessage = null;

        if (!HasFolder)
        {
            _reading = Reading.Empty;
            _series = [];
            Apply();
            return;
        }

        if (!Directory.Exists(Folder))
        {
            ErrorMessage = _host.Text.Format("tin.error.nofolder", Folder);
            _reading = Reading.Empty;
            _series = [];
            Apply();
            return;
        }

        var saved = _settings.Columns.ToDictionary(
            pair => pair.Key,
            pair => new ColumnMap(pair.Value.Date, pair.Value.Name, pair.Value.Amount, pair.Value.Reference),
            StringComparer.Ordinal);

        _reading = Statement.Read(Folder, saved, _settings.FileAccounts);

        // An account that has gone away, because its files were moved out of the folder, should
        // not leave the tab showing an empty list and no way back.
        if (_account.Length > 0 && !_reading.Accounts.Contains(_account, StringComparer.Ordinal))
            _account = "";

        Apply();

        Status = _reading.Sources.Count == 0
            ? _host.Text["tin.status.nofiles"]
            : _host.Text.Format("tin.status.read", _series.Count);

        _host.Log($"Tin read {_reading.Sources.Count} file(s), {_reading.Charges.Count} charge(s), " +
                  $"{_series.Count} recurring, {_reading.Duplicates} duplicate row(s) skipped");
    }

    private void Apply()
    {
        // Only what belongs to the account being looked at, which is also what makes a monthly
        // total mean anything when a folder holds two people's statements.
        _series = Recurring.Find(Mine(_reading.Charges), DateTime.Now);

        Sources.Clear();
        foreach (var source in _reading.Sources.Where(s => _account.Length == 0 || s.Account == _account))
            Sources.Add(new SourceViewModel(source));

        if (SelectedSource is { } chosen && Sources.All(s => s.File.Path != chosen.File.Path))
            SelectedSource = null;

        BuildAccounts();
        BuildBuckets();
        BuildChart();
        Rebuild();
    }

    /// <summary>
    /// The ring and the two forecasts. All of it reads the charges already in memory, so it is
    /// rebuilt whenever the account changes rather than being carried around stale.
    /// </summary>
    private void BuildChart()
    {
        var mine = Mine(_reading.Charges).ToList();

        Slices.Clear();
        var index = 0;
        foreach (var slice in Spending.ByPayee(mine))
            Slices.Add(new SliceViewModel(slice, index++, Currency));

        _out = Spending.Monthly(mine, DateTime.Now, incoming: false);
        _in = Spending.Monthly(mine, DateTime.Now, incoming: true);

        OnEverythingChanged();
    }

    /// <summary>The charges of the account being looked at, or all of them.</summary>
    private IEnumerable<Charge> Mine(IEnumerable<Charge> charges) => _account.Length == 0
        ? charges
        : charges.Where(c => _reading.AccountOf(c) == _account);

    private void BuildAccounts()
    {
        Accounts.Clear();
        AccountChoices.Clear();

        var found = _reading.Accounts;

        // "Everything" only when there is more than one, since a chooser with one entry on it is
        // a chooser nobody needs.
        if (found.Count > 1)
        {
            Accounts.Add(new AccountViewModel("", null,
                _reading.Sources.Count, _reading.Charges.Count,
                Recurring.Find(_reading.Charges, DateTime.Now).Sum(s => s.Ongoing), Currency));
        }

        AccountChoices.Add(new AccountChoice("", ""));

        foreach (var key in found)
        {
            var files = _reading.Sources.Count(s => s.Account == key);
            var charges = _reading.Charges.Where(c => _reading.AccountOf(c) == key).ToList();
            var named = _settings.AccountNames.GetValueOrDefault(key, "");

            Accounts.Add(new AccountViewModel(key, named, files, charges.Count,
                Recurring.Find(charges, DateTime.Now).Sum(s => s.Ongoing), Currency));

            AccountChoices.Add(new AccountChoice(key,
                named.Length > 0 ? $"{named} ({Iban.Mask(key)})" : Iban.Mask(key)));
        }

        foreach (var account in Accounts)
            account.IsOn = account.Key == _account;

        OnPropertyChanged(nameof(HasAccounts));
        OnPropertyChanged(nameof(AccountName));
        OnPropertyChanged(nameof(IsShowingOneAccount));
        OnPropertyChanged(nameof(SourceAccount));
    }

    private void ShowAccount(string key)
    {
        if (_account == key)
            return;

        _account = key;
        _settings.Account = key;
        Save();

        Selected = null;
        Apply();
        Status = "";
    }

    private IEnumerable<Series> Visible(string filter)
    {
        var hidden = _settings.Ignored.ToHashSet(StringComparer.Ordinal);

        return filter switch
        {
            BucketHidden => _series.Where(s => hidden.Contains(s.Key)),
            BucketUp => _series.Where(s => !hidden.Contains(s.Key) && s.WentUp),
            BucketStopped => _series.Where(s => !hidden.Contains(s.Key) && s.Lapsed),
            _ => _series.Where(s => !hidden.Contains(s.Key)),
        };
    }

    private void BuildBuckets()
    {
        Buckets.Clear();

        foreach (var key in new[] { BucketAll, BucketUp, BucketStopped, BucketHidden })
        {
            var matching = Visible(key).ToList();

            // Everything is always offered; the other three only when they have something in
            // them, so an account with no price rises does not show an empty accusation.
            if (key != BucketAll && matching.Count == 0)
                continue;

            var detail = key == BucketStopped || key == BucketHidden
                ? MeowsText.Current.Format("tin.bucket.count", matching.Count)
                : MeowsText.Current.Format("tin.bucket.month", matching.Count,
                    Services.Money.Show(matching.Sum(s => s.Ongoing), Currency, Shown.Culture));

            Buckets.Add(new BucketViewModel("tin.bucket." + key, key, matching.Count, detail));
        }
    }

    private void ApplyFilter(string? key)
    {
        _filter = key ?? BucketAll;
        OnPropertyChanged(nameof(IsShowingHidden));
        Rebuild();
    }

    private void Rebuild()
    {
        var was = Selected?.Key;

        Series.Clear();
        foreach (var series in Visible(_filter))
            Series.Add(new SeriesViewModel(series, Currency));

        Arrange();

        foreach (var bucket in Buckets)
            bucket.IsOn = bucket.Key == _filter;

        Selected = Rows.FirstOrDefault(r => r.Key == was);

        OnPropertyChanged(nameof(IsEmpty));
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(MonthlyTotal));
        OnPropertyChanged(nameof(HasTotal));
        OnPropertyChanged(nameof(Advice));
        OnPropertyChanged(nameof(HasAdvice));
        IgnoreCommand.RaiseCanExecuteChanged();
        RestoreCommand.RaiseCanExecuteChanged();
    }

    /// <summary>
    /// Puts the subscriptions into the order they are read in: a company with several of them
    /// gets a row of its own with the sum, and its parts indented under it; everything else stands
    /// alone. Companies and singles are sorted together by what they cost a month.
    ///
    /// Grouped by company rather than by subscription key, so the two spellings an energy company
    /// uses for itself land under one row rather than two.
    /// </summary>
    private void Arrange()
    {
        Rows.Clear();

        var families = Series
            .GroupBy(s => Recurring.KeyOf(s.Series.Charges[^1].Name, Recurring.Company), StringComparer.Ordinal)
            .Select(g => g.ToList())
            .ToList();

        var ordered = families
            .Select(members => (Members: members, PerMonth: members.Sum(m => m.Series.PerMonth)))
            .OrderByDescending(f => f.PerMonth);

        foreach (var (members, _) in ordered)
        {
            if (members.Count == 1)
            {
                members[0].IsChild = false;
                Rows.Add(members[0]);
                continue;
            }

            var key = "family:" + Recurring.KeyOf(members[0].Series.Charges[^1].Name, Recurring.Company);
            var name = Recurring.NameOf(members[0].Series.Charges[^1].Name);

            Rows.Add(new FamilyViewModel(key, name, members, Currency));

            foreach (var member in members.OrderByDescending(m => m.Series.PerMonth))
            {
                member.IsChild = true;
                Rows.Add(member);
            }
        }
    }

    private void ShowCharges()
    {
        Charges.Clear();
        if (Selected is not { } selected)
            return;

        // Newest first: the interesting one is the most recent, and the rest are the evidence.
        foreach (var charge in selected.AllCharges.OrderByDescending(c => c.Date))
            Charges.Add(new ChargeViewModel(charge, Currency));
    }

    /// <summary>
    /// Hides a series. Rent, tax and a standing transfer into savings all repeat exactly like a
    /// subscription does, and nothing in the file says which is which, so the list is only useful
    /// once the things you already know about can be put away.
    /// </summary>
    private void Ignore()
    {
        if (Selected is not { } selected)
            return;

        // A company row puts away everything under it; a part puts away only itself.
        foreach (var key in selected.Keys.Where(k => !_settings.Ignored.Contains(k)))
            _settings.Ignored.Add(key);

        Save();
        BuildBuckets();
        Rebuild();
        Status = _host.Text.Format("tin.status.hidden", selected.Name);
    }

    private void Restore()
    {
        if (Selected is not { } selected)
            return;

        foreach (var key in selected.Keys)
            _settings.Ignored.Remove(key);

        Save();
        BuildBuckets();
        Rebuild();
        Status = _host.Text.Format("tin.status.restored", selected.Name);
    }

    /// <summary>
    /// A language change repaints text that {m:Tr} looks after on its own, but everything worked
    /// out in code has to be asked again, including inside every row.
    /// </summary>
    private void Retranslate()
    {
        foreach (var row in Rows)
            row.Reread();
        foreach (var charge in Charges)
            charge.Reread();
        foreach (var source in Sources)
            source.Reread();
        foreach (var account in Accounts)
            account.Reread();
        foreach (var choice in AccountChoices)
            choice.Reread();
        foreach (var slice in Slices)
            slice.Reread();

        // The bucket details carry formatted money, so they are rebuilt rather than reread.
        BuildBuckets();
        foreach (var bucket in Buckets)
            bucket.IsOn = bucket.Key == _filter;

        OnEverythingChanged();
    }

    private void Open(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return;

        try
        {
            Explorer.Open(path);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("tin.error.open", path, ex.Message);
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
            _host.Log($"Could not save Tin settings: {ex.Message}");
        }
    }

    /// <summary>Ctrl+K reaching into the rows on screen: a payee by name. Landing on one selects it.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        var hits = new List<SearchHit>();

        foreach (var row in Rows)
        {
            if (hits.Count >= limit)
                break;
            if (!SearchWords.Match(words, row.Name))
                continue;

            var chosen = row;
            hits.Add(new SearchHit(row.Name, row.RiseText, () => Selected = chosen));
        }

        return hits;
    }

    public void Dispose() => _language.Dispose();
}
