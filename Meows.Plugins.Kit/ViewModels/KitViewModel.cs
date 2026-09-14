using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Meows.Media;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Kit.Services;
using SkiaSharp;

namespace Meows.Plugins.Kit.ViewModels;

public sealed class KitSettings
{
    /// <summary>Where the kits live: one folder per one-shot.</summary>
    public string? Root { get; set; }

    /// <summary>Where exports go. Null means beside the kits, under Exports.</summary>
    public string? ExportRoot { get; set; }

    /// <summary>Roll20's per-file limit for your plan, in megabytes. It moves, so it is a setting.</summary>
    public int Roll20MaxMegabytes { get; set; } = 10;

    /// <summary>Longest side after Fit. 4096 is what a browser handles without complaint.</summary>
    public int FitMaxSide { get; set; } = 4096;
}

/// <summary>A one-shot folder in the left column.</summary>
public sealed class KitEntryViewModel(string folder)
{
    public string Folder { get; } = folder;

    public string Name => Path.GetFileName(Folder.TrimEnd(Path.DirectorySeparatorChar));
}

public enum ItemKind
{
    Map,
    Token,
    Handout,
}

/// <summary>One picture in the open kit, with a thumbnail and the words for its row.</summary>
public sealed class ItemViewModel(KitItem item, ItemKind kind, string kitFolder) : ObservableObject, IDisposable
{
    private Bitmap? _thumbnail;

    public KitItem Item { get; } = item;

    public ItemKind Kind { get; } = kind;

    public string Path => System.IO.Path.Combine(kitFolder, Item.File.Replace('/', System.IO.Path.DirectorySeparatorChar));

    public string Name => Item.Name;

    public string FileName => Item.FileName;

    public bool IsMap => Kind == ItemKind.Map;

    public bool IsToken => Kind == ItemKind.Token;

    public bool IsHandout => Kind == ItemKind.Handout;

    public string Glyph => Kind switch { ItemKind.Map => "🗺", ItemKind.Token => "🪙", _ => "📜" };

    /// <summary>Size, and for a map its squares or that it has none.</summary>
    public string Detail
    {
        get
        {
            var text = MeowsText.Current;
            var size = Item.Width > 0 ? $"{Item.Width} × {Item.Height}" : "";
            if (!IsMap || Item.Grid is null)
                return size;
            if (!Item.Grid.Enabled)
                return $"{size} · {text["kit.grid.none"]}";
            var (c, r) = Pictures.Squares(Item.Width - 2 * Item.Padding, Item.Height - 2 * Item.Padding, Item.Grid);
            return $"{size} · {text.Format("kit.grid.squares", c, r, (int)Math.Round(Item.Grid.Size))}";
        }
    }

    public Bitmap? Thumbnail
    {
        get => _thumbnail;
        private set
        {
            var old = _thumbnail;
            if (SetField(ref _thumbnail, value))
            {
                OnPropertyChanged(nameof(HasThumbnail));
                old?.Dispose();
            }
        }
    }

    public bool HasThumbnail => _thumbnail is not null;

    public async Task LoadThumbnailAsync(int width, CancellationToken token)
    {
        var path = Path;
        var bitmap = await Task.Run(() => Thumbnails.FromFile(path, width), token).ConfigureAwait(true);
        if (token.IsCancellationRequested)
        {
            bitmap?.Dispose();
            return;
        }
        Thumbnail = bitmap;
    }

    public void Reread() => OnEverythingChanged();

    public void Dispose() => Thumbnail = null;
}

/// <summary>A frame to pick from, by its label.</summary>
/// <summary>A map on the run sheet's combo: its file, and the name the card shows. File empty means no map.</summary>
public sealed record MapChoice(string File, string Name)
{
    public override string ToString() => Name;
}

/// <summary>One markdown file under notes/.</summary>
public sealed class NoteViewModel(string path)
{
    public string Path { get; } = path;

    public string Name => System.IO.Path.GetFileNameWithoutExtension(Path);
}

/// <summary>One fight on the run sheet, edited in place; every change goes straight to the manifest.</summary>
public sealed class EncounterViewModel(Encounter encounter, Func<IReadOnlyList<MapChoice>> maps, Func<IReadOnlyList<KitItem>> tokens, Action changed) : ObservableObject
{
    public Encounter Encounter { get; } = encounter;

    public IReadOnlyList<MapChoice> Maps => maps();

    public string Name
    {
        get => Encounter.Name;
        set
        {
            if (Encounter.Name == (value ?? ""))
                return;
            Encounter.Name = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(Title));
            changed();
        }
    }

    public MapChoice Map
    {
        get => Maps.FirstOrDefault(m => string.Equals(m.File, Encounter.Map, StringComparison.OrdinalIgnoreCase)) ?? Maps[0];
        set
        {
            var file = value?.File ?? "";
            if (Encounter.Map == file)
                return;
            Encounter.Map = file;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Detail));
            changed();
        }
    }

    public string Roster
    {
        get => Encounter.Roster;
        set
        {
            if (Encounter.Roster == (value ?? ""))
                return;
            Encounter.Roster = value ?? "";
            OnPropertyChanged();
            OnPropertyChanged(nameof(Detail));
            changed();
        }
    }

    public string Notes
    {
        get => Encounter.Notes;
        set
        {
            if (Encounter.Notes == (value ?? ""))
                return;
            Encounter.Notes = value ?? "";
            OnPropertyChanged();
            changed();
        }
    }

    public string Title => Encounter.Name.Length > 0 ? Encounter.Name : MeowsText.Current["kit.encounter.unnamed"];

    /// <summary>"Tavern · 3 × Goblin, Bugbear", for the row.</summary>
    public string Detail
    {
        get
        {
            var map = Maps.FirstOrDefault(m => m.File.Length > 0 && string.Equals(m.File, Encounter.Map, StringComparison.OrdinalIgnoreCase));
            var roster = RunSheet.Summarise(RunSheet.Parse(Encounter.Roster, tokens()));
            return string.Join(" · ", new[] { map?.Name, roster }.Where(x => !string.IsNullOrEmpty(x)));
        }
    }

    public void Reread() => OnEverythingChanged();
}

public sealed record FrameChoice(string? File, string Label)
{
    public override string ToString() => Label;
}

/// <summary>
/// The one-shot tab: a folder per night, the pictures in it named, gridded or not, fitted,
/// framed and cut, and written out the way Foundry or Roll20 wants them. Every change is a
/// change to a file; the manifest only remembers what a file cannot.
/// </summary>
public sealed class KitViewModel : ObservableObject, IDisposable, ISearchable, IHandoffTarget
{
    private const int ThumbnailWidth = 96;
    private const int PreviewWidth = 640;

    private readonly IMeowsHost _host;
    private readonly KitSettings _settings;
    private readonly LanguageWatch _language;
    private CancellationTokenSource? _thumbnails;
    private CancellationTokenSource? _preview;

    private KitEntryViewModel? _selectedKit;
    private KitManifest? _manifest;
    private ItemViewModel? _selected;
    private Bitmap? _previewImage;
    private string _newKitName = "";
    private string? _status;
    private string? _errorMessage;
    private bool _isBusy;
    private ItemKind _addAs = ItemKind.Map;

    // Editing state for the selected item, bound to the card.
    private string _editName = "";
    private string _editCaption = "";
    private bool _gridEnabled = true;
    private double _gridSize = 100;
    private double _gridOffsetX;
    private double _gridOffsetY;
    private FrameChoice _border = NoFrame;
    private FrameChoice _ring = NoFrame;
    private FrameChoice _background = NoFrame;
    private FrameSet _frames = FrameSet.Shipped;

    // The run sheet.
    private bool _showRunSheet;
    private NoteViewModel? _selectedNote;
    private string _noteText = "";
    private bool _noteDirty;
    private string _newNoteName = "";
    private EncounterViewModel? _selectedEncounter;
    private double _zoom = 1;
    private double _offsetX;
    private double _offsetY;
    private string _side = "";

    private static readonly FrameChoice NoFrame = new(null, "");

    public KitViewModel(IMeowsHost host)
    {
        _host = host;
        _settings = host.LoadSettings<KitSettings>() ?? new KitSettings();
        _settings.Root ??= Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Oneshots");

        NewKitCommand = new RelayCommand(NewKit, () => NewKitName.Trim().Length > 0);
        RefreshCommand = new RelayCommand(Reload);
        OpenKitFolderCommand = new RelayCommand(() => Open(SelectedKit?.Folder), () => SelectedKit is not null);
        RenameCommand = new RelayCommand(Rename, () => Selected is not null && EditName.Trim().Length > 0);
        GuessGridCommand = new RelayCommand(GuessGrid, () => Selected is { IsMap: true });
        ApplyGridCommand = new RelayCommand(ApplyGrid, () => Selected is { IsMap: true });
        FitCommand = new RelayCommand(() => _ = FitAsync(), () => Selected is not null && !IsBusy);
        FrameCommand = new RelayCommand(() => _ = FrameAsync(), () => Selected is { IsToken: false } && Border.File is not null && !IsBusy);
        MakeTokenCommand = new RelayCommand(() => _ = MakeTokenAsync(), () => Selected is { IsToken: true } && !IsBusy);
        MoveUpCommand = new RelayCommand(() => Move(-1), () => Selected is not null);
        MoveDownCommand = new RelayCommand(() => Move(+1), () => Selected is not null);
        RemoveCommand = new RelayCommand(Remove, () => Selected is not null && !IsBusy);
        ExportFoundryCommand = new RelayCommand(() => _ = ExportAsync(foundry: true), () => _manifest is not null && !IsBusy);
        ExportRoll20Command = new RelayCommand(() => _ = ExportAsync(foundry: false), () => _manifest is not null && !IsBusy);
        OpenExportsCommand = new RelayCommand(() => Open(ExportRoot));
        OpenFramesFolderCommand = new RelayCommand(() => Open(FramesFolder));
        ShowPicturesCommand = new RelayCommand(() => ShowRunSheet = false);
        ShowRunSheetCommand = new RelayCommand(() => ShowRunSheet = true);

        SaveNoteCommand = new RelayCommand(SaveNote, () => SelectedNote is not null && _noteDirty);
        NewNoteCommand = new RelayCommand(NewNote, () => HasKit && NewNoteName.Trim().Length > 0);
        AddEncounterCommand = new RelayCommand(AddEncounter, () => HasKit);
        RemoveEncounterCommand = new RelayCommand(RemoveEncounter, () => SelectedEncounter is not null);
        EncounterUpCommand = new RelayCommand(() => MoveEncounter(-1), () => SelectedEncounter is not null);
        EncounterDownCommand = new RelayCommand(() => MoveEncounter(+1), () => SelectedEncounter is not null);

        Reload();
        _language = new LanguageWatch(() =>
        {
            OnEverythingChanged();
            foreach (var item in Items)
                item.Reread();
            foreach (var encounter in Encounters)
                encounter.Reread();
        });
    }

    /// <summary>The user's own frames live here, beside the kits, and are read again on every Refresh.</summary>
    public string FramesFolder => Path.Combine(Root, FrameSet.UserFolderName);

    private void LoadFrames()
    {
        try
        {
            _frames = FrameSet.WithUserFolder(FramesFolder);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not read the frames folder: {ex.Message}");
            _frames = FrameSet.Shipped;
        }
        Borders = [NoFrame, .. _frames.Borders.Select(b => new FrameChoice(b.File, b.Label))];
        Rings = [NoFrame, .. _frames.Tokens.Select(t => new FrameChoice(t.File, t.Label))];
        Backgrounds = [NoFrame, .. _frames.Backgrounds.Select(b => new FrameChoice(b.File, b.Label))];
        OnPropertyChanged(nameof(Borders));
        OnPropertyChanged(nameof(Rings));
        OnPropertyChanged(nameof(Backgrounds));
        OnPropertyChanged(nameof(FramesFolder));
        OnPropertyChanged(nameof(FramesText));
    }

    /// <summary>"16 rings, 7 backgrounds, 5 borders, 2 of them yours."</summary>
    public string FramesText
    {
        get
        {
            var own = _frames.Tokens.Count(t => t.Path is not null) + _frames.Backgrounds.Count(b => b.Path is not null) + _frames.Borders.Count(b => b.Path is not null);
            return _host.Text.Format("kit.frames.count", _frames.Tokens.Count, _frames.Backgrounds.Count, _frames.Borders.Count, own);
        }
    }

    // ---- the kits ----

    public ObservableCollection<KitEntryViewModel> Kits { get; } = [];

    public ObservableCollection<ItemViewModel> Items { get; } = [];

    public IReadOnlyList<FrameChoice> Borders { get; private set; } = [NoFrame];

    public IReadOnlyList<FrameChoice> Rings { get; private set; } = [NoFrame];

    public IReadOnlyList<FrameChoice> Backgrounds { get; private set; } = [NoFrame];

    public ObservableCollection<NoteViewModel> Notes { get; } = [];

    public ObservableCollection<EncounterViewModel> Encounters { get; } = [];

    public RelayCommand OpenFramesFolderCommand { get; }

    public RelayCommand ShowPicturesCommand { get; }

    public RelayCommand ShowRunSheetCommand { get; }

    public RelayCommand SaveNoteCommand { get; }

    public RelayCommand NewNoteCommand { get; }

    public RelayCommand AddEncounterCommand { get; }

    public RelayCommand RemoveEncounterCommand { get; }

    public RelayCommand EncounterUpCommand { get; }

    public RelayCommand EncounterDownCommand { get; }

    public IReadOnlyList<string> Sides { get; } = ["", "friend", "foe", "neutral", "boss"];

    public IReadOnlyList<ItemKind> Kinds { get; } = [ItemKind.Map, ItemKind.Token, ItemKind.Handout];

    public IReadOnlyList<int> FitSizes { get; } = [2048, 4096, 8192];

    public RelayCommand NewKitCommand { get; }

    public RelayCommand RefreshCommand { get; }

    public RelayCommand OpenKitFolderCommand { get; }

    public RelayCommand RenameCommand { get; }

    public RelayCommand GuessGridCommand { get; }

    public RelayCommand ApplyGridCommand { get; }

    public RelayCommand FitCommand { get; }

    public RelayCommand FrameCommand { get; }

    public RelayCommand MakeTokenCommand { get; }

    public RelayCommand MoveUpCommand { get; }

    public RelayCommand MoveDownCommand { get; }

    public RelayCommand RemoveCommand { get; }

    public RelayCommand ExportFoundryCommand { get; }

    public RelayCommand ExportRoll20Command { get; }

    public RelayCommand OpenExportsCommand { get; }

    public string Root => _settings.Root!;

    public string ExportRoot => _settings.ExportRoot ?? Path.Combine(Root, "Exports");

    public int Roll20MaxMegabytes
    {
        get => _settings.Roll20MaxMegabytes;
        set
        {
            if (value <= 0 || _settings.Roll20MaxMegabytes == value)
                return;
            _settings.Roll20MaxMegabytes = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public int FitMaxSide
    {
        get => _settings.FitMaxSide;
        set
        {
            if (_settings.FitMaxSide == value)
                return;
            _settings.FitMaxSide = value;
            SaveSettings();
            OnPropertyChanged();
        }
    }

    public string NewKitName
    {
        get => _newKitName;
        set
        {
            if (SetField(ref _newKitName, value ?? ""))
                NewKitCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>What *Add pictures* and a handoff put the pictures in as.</summary>
    public ItemKind AddAs
    {
        get => _addAs;
        set => SetField(ref _addAs, value);
    }

    public KitEntryViewModel? SelectedKit
    {
        get => _selectedKit;
        set
        {
            if (!SetField(ref _selectedKit, value))
                return;
            OnPropertyChanged(nameof(HasKit));
            OnPropertyChanged(nameof(KitTitle));
            OpenKitFolderCommand.RaiseCanExecuteChanged();
            ExportFoundryCommand.RaiseCanExecuteChanged();
            ExportRoll20Command.RaiseCanExecuteChanged();
            LoadKit();
        }
    }

    public bool HasKit => _selectedKit is not null && _manifest is not null;

    public string KitTitle => _manifest?.Title ?? "";

    public bool IsEmpty => Items.Count == 0;

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (SetField(ref _isBusy, value))
                RaiseCommands();
        }
    }

    public string Status
    {
        get => _status ?? _host.Text["kit.status.start"];
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

    public bool HasError => !string.IsNullOrEmpty(_errorMessage);

    public void SetRoot(string folder)
    {
        _settings.Root = folder;
        SaveSettings();
        OnPropertyChanged(nameof(Root));
        OnPropertyChanged(nameof(ExportRoot));
        Reload();
    }

    public void SetExportRoot(string folder)
    {
        _settings.ExportRoot = folder;
        SaveSettings();
        OnPropertyChanged(nameof(ExportRoot));
    }

    private void Reload()
    {
        var keep = SelectedKit?.Folder;
        Kits.Clear();
        try
        {
            Directory.CreateDirectory(Root);
            LoadFrames();
            foreach (var folder in Directory.EnumerateDirectories(Root).OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase))
            {
                if (string.Equals(Path.GetFileName(folder), "Exports", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(Path.GetFileName(folder), FrameSet.UserFolderName, StringComparison.OrdinalIgnoreCase))
                    continue;
                Kits.Add(new KitEntryViewModel(folder));
            }
            ErrorMessage = null;
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("kit.error.root", Root, ex.Message);
        }

        SelectedKit = Kits.FirstOrDefault(k => string.Equals(k.Folder, keep, StringComparison.OrdinalIgnoreCase)) ?? Kits.FirstOrDefault();
        if (SelectedKit is not null && string.Equals(SelectedKit.Folder, keep, StringComparison.OrdinalIgnoreCase))
            LoadKit();
    }

    private void NewKit()
    {
        var name = Exporter.Slug(NewKitName);
        var folder = Path.Combine(Root, name);
        try
        {
            foreach (var sub in new[] { "maps", "tokens", "handouts", "notes" })
                Directory.CreateDirectory(Path.Combine(folder, sub));
            new KitManifest { Title = name }.Save(folder);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("kit.error.create", ex.Message);
            return;
        }

        NewKitName = "";
        Reload();
        SelectedKit = Kits.FirstOrDefault(k => string.Equals(k.Folder, folder, StringComparison.OrdinalIgnoreCase));
        _host.Store.Record("created", folder, _host.Text.Format("kit.journal.created", name));
    }

    // ---- the open kit ----

    private void LoadKit()
    {
        _thumbnails?.Cancel();
        SaveNote();
        Selected = null;
        foreach (var item in Items)
            item.Dispose();
        Items.Clear();
        SelectedNote = null;
        Notes.Clear();
        SelectedEncounter = null;
        Encounters.Clear();
        _manifest = null;

        if (SelectedKit is not { } kit)
        {
            RaiseKitState();
            return;
        }

        try
        {
            _manifest = KitManifest.Load(kit.Folder);
            if (_manifest.Reconcile(kit.Folder))
                _manifest.Save(kit.Folder);
            MeasureUnmeasured(kit.Folder);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("kit.error.load", ex.Message);
            RaiseKitState();
            return;
        }

        foreach (var map in _manifest.Maps)
            Items.Add(new ItemViewModel(map, ItemKind.Map, kit.Folder));
        foreach (var token in _manifest.Tokens)
            Items.Add(new ItemViewModel(token, ItemKind.Token, kit.Folder));
        foreach (var handout in _manifest.Handouts)
            Items.Add(new ItemViewModel(handout, ItemKind.Handout, kit.Folder));

        foreach (var note in _manifest.Notes)
            Notes.Add(new NoteViewModel(Path.Combine(kit.Folder, note.Replace('/', Path.DirectorySeparatorChar))));
        foreach (var encounter in _manifest.Encounters)
            Encounters.Add(new EncounterViewModel(encounter, () => MapChoices, () => _manifest?.Tokens ?? [], SaveManifest));
        SelectedNote = Notes.FirstOrDefault();

        Status = _host.Text.Format("kit.status.loaded", _manifest.Maps.Count, _manifest.Tokens.Count, _manifest.Handouts.Count, _manifest.Notes.Count);
        RaiseKitState();
        _ = LoadThumbnailsAsync();
    }

    // ---- the run sheet ----

    /// <summary>The middle column shows the pictures or the run sheet; the right column stays.</summary>
    public bool ShowRunSheet
    {
        get => _showRunSheet;
        set
        {
            if (SetField(ref _showRunSheet, value))
                OnPropertyChanged(nameof(ShowPictures));
        }
    }

    public bool ShowPictures => !_showRunSheet;

    /// <summary>The maps a fight can be on, with "no map" first.</summary>
    public IReadOnlyList<MapChoice> MapChoices =>
        [new MapChoice("", _host.Text["kit.encounter.nomap"]), .. (_manifest?.Maps ?? []).Select(m => new MapChoice(m.File, m.Name))];

    public NoteViewModel? SelectedNote
    {
        get => _selectedNote;
        set
        {
            if (ReferenceEquals(_selectedNote, value))
                return;
            SaveNote();
            _selectedNote = value;
            _noteText = value is null ? "" : SafeReadText(value.Path);
            _noteDirty = false;
            OnPropertyChanged();
            OnPropertyChanged(nameof(NoteText));
            OnPropertyChanged(nameof(HasNote));
            OnPropertyChanged(nameof(NoteDirty));
            SaveNoteCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasNote => _selectedNote is not null;

    public string NoteText
    {
        get => _noteText;
        set
        {
            if (!SetField(ref _noteText, value ?? ""))
                return;
            _noteDirty = true;
            OnPropertyChanged(nameof(NoteDirty));
            SaveNoteCommand.RaiseCanExecuteChanged();
        }
    }

    public bool NoteDirty => _noteDirty;

    public string NewNoteName
    {
        get => _newNoteName;
        set
        {
            if (SetField(ref _newNoteName, value ?? ""))
                NewNoteCommand.RaiseCanExecuteChanged();
        }
    }

    /// <summary>The note as typed, written over its file. Called on Save, on switching notes or kits, and on closing.</summary>
    public void SaveNote()
    {
        if (!_noteDirty || _selectedNote is not { } note)
            return;
        try
        {
            File.WriteAllText(note.Path, _noteText);
            _noteDirty = false;
            OnPropertyChanged(nameof(NoteDirty));
            SaveNoteCommand.RaiseCanExecuteChanged();
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("kit.error.note", ex.Message);
        }
    }

    private void NewNote()
    {
        if (SelectedKit is not { } kit || _manifest is null)
            return;
        var name = Exporter.Slug(NewNoteName);
        var path = Path.Combine(kit.Folder, "notes", name + ".md");
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            if (!File.Exists(path))
                File.WriteAllText(path, $"# {name}\n\n");
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("kit.error.note", ex.Message);
            return;
        }

        NewNoteName = "";
        if (_manifest.Reconcile(kit.Folder))
            SaveManifest();
        Notes.Clear();
        foreach (var note in _manifest.Notes)
            Notes.Add(new NoteViewModel(Path.Combine(kit.Folder, note.Replace('/', Path.DirectorySeparatorChar))));
        SelectedNote = Notes.FirstOrDefault(n => string.Equals(n.Path, path, StringComparison.OrdinalIgnoreCase));
    }

    public EncounterViewModel? SelectedEncounter
    {
        get => _selectedEncounter;
        set
        {
            if (!SetField(ref _selectedEncounter, value))
                return;
            OnPropertyChanged(nameof(HasEncounter));
            RemoveEncounterCommand.RaiseCanExecuteChanged();
            EncounterUpCommand.RaiseCanExecuteChanged();
            EncounterDownCommand.RaiseCanExecuteChanged();
        }
    }

    public bool HasEncounter => _selectedEncounter is not null;

    private void AddEncounter()
    {
        if (_manifest is null)
            return;
        var encounter = new Encounter
        {
            Name = _host.Text.Format("kit.encounter.new", _manifest.Encounters.Count + 1),
            Map = Selected is { IsMap: true } map ? map.Item.File : _manifest.Maps.FirstOrDefault()?.File ?? "",
        };
        _manifest.Encounters.Add(encounter);
        SaveManifest();
        var model = new EncounterViewModel(encounter, () => MapChoices, () => _manifest?.Tokens ?? [], SaveManifest);
        Encounters.Add(model);
        SelectedEncounter = model;
    }

    private void RemoveEncounter()
    {
        if (_manifest is null || SelectedEncounter is not { } encounter)
            return;
        var index = Encounters.IndexOf(encounter);
        _manifest.Encounters.Remove(encounter.Encounter);
        Encounters.Remove(encounter);
        SaveManifest();
        SelectedEncounter = Encounters.ElementAtOrDefault(Math.Min(index, Encounters.Count - 1));
    }

    private void MoveEncounter(int delta)
    {
        if (_manifest is null || SelectedEncounter is not { } encounter)
            return;
        var from = Encounters.IndexOf(encounter);
        var to = from + delta;
        if (from < 0 || to < 0 || to >= Encounters.Count)
            return;
        Encounters.Move(from, to);
        _manifest.Encounters.RemoveAt(from);
        _manifest.Encounters.Insert(to, encounter.Encounter);
        SaveManifest();
    }

    private static string SafeReadText(string path)
    {
        try
        {
            return File.ReadAllText(path);
        }
        catch (Exception)
        {
            return "";
        }
    }

    /// <summary>A picture that was never measured gets its size from its header, and a map its first grid guess.</summary>
    private void MeasureUnmeasured(string folder)
    {
        var changed = false;
        foreach (var item in _manifest!.All.Where(i => i.Width == 0))
        {
            var path = Path.Combine(folder, item.File.Replace('/', Path.DirectorySeparatorChar));
            using var bitmap = Preparer.Decode(SafeRead(path));
            if (bitmap is null)
                continue;
            item.Width = bitmap.Width;
            item.Height = bitmap.Height;
            if (item.Grid is { } grid && _manifest.Maps.Contains(item))
                grid.Size = Pictures.GuessGrid(bitmap.Width, bitmap.Height);
            changed = true;
        }
        if (changed)
            _manifest.Save(folder);
    }

    private static byte[] SafeRead(string path)
    {
        try
        {
            return File.ReadAllBytes(path);
        }
        catch (Exception)
        {
            return [];
        }
    }

    private void RaiseKitState()
    {
        OnPropertyChanged(nameof(MapChoices));
        NewNoteCommand.RaiseCanExecuteChanged();
        AddEncounterCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(HasKit));
        OnPropertyChanged(nameof(KitTitle));
        OnPropertyChanged(nameof(IsEmpty));
        RaiseCommands();
    }

    private async Task LoadThumbnailsAsync()
    {
        _thumbnails?.Cancel();
        _thumbnails = new CancellationTokenSource();
        var token = _thumbnails.Token;
        try
        {
            foreach (var item in Items.ToList())
            {
                if (token.IsCancellationRequested)
                    return;
                await item.LoadThumbnailAsync(ThumbnailWidth, token);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    // ---- adding pictures ----

    /// <summary>Copies pictures into the kit under the kind chosen, never moving the originals.</summary>
    public void AddPictures(IEnumerable<string> paths, ItemKind? kind = null)
    {
        if (SelectedKit is not { } kit || _manifest is null)
            return;

        var sub = (kind ?? AddAs) switch { ItemKind.Map => "maps", ItemKind.Token => "tokens", _ => "handouts" };
        var folder = Path.Combine(kit.Folder, sub);
        Directory.CreateDirectory(folder);
        var added = 0;

        foreach (var path in paths.Where(KitManifest.IsPicture))
        {
            try
            {
                var target = Unique(Path.Combine(folder, Path.GetFileName(path)));
                File.Copy(path, target);
                added++;
            }
            catch (Exception ex)
            {
                _host.Log(LogLevel.Warning, $"Could not add {path} to the kit: {ex.Message}");
            }
        }

        if (added == 0)
            return;

        LoadKit();
        Status = _host.Text.Format("kit.status.added", added, sub);
        _host.Store.Record("added", kit.Folder, _host.Text.Format("kit.journal.added", added, _manifest.Title));
    }

    public bool Accepts(Handoff handoff) =>
        handoff.Verb == HandoffVerbs.Files && handoff.Paths.Count > 0 && HasKit;

    public void Receive(Handoff handoff)
    {
        if (!Accepts(handoff))
            return;
        var before = Items.Count;
        AddPictures(handoff.Paths);
        handoff.Answer(_host.Text.Format("kit.reply.added", Items.Count - before, KitTitle));
    }

    // ---- the selected picture ----

    public ItemViewModel? Selected
    {
        get => _selected;
        set
        {
            if (!SetField(ref _selected, value))
                return;
            OnPropertyChanged(nameof(HasSelection));
            ReadEditsFrom(value);
            RaiseCommands();
            _ = ShowPreviewAsync();
        }
    }

    public bool HasSelection => _selected is not null;

    private void ReadEditsFrom(ItemViewModel? item)
    {
        _editName = item?.Item.Name ?? "";
        _editCaption = item?.Item.Caption ?? "";
        var grid = item?.Item.Grid;
        _gridEnabled = grid?.Enabled ?? false;
        _gridSize = grid?.Size ?? 100;
        _gridOffsetX = grid?.OffsetX ?? 0;
        _gridOffsetY = grid?.OffsetY ?? 0;
        _border = Borders.FirstOrDefault(b => b.File == item?.Item.Frame && item?.Kind != ItemKind.Token) ?? NoFrame;
        _ring = Rings.FirstOrDefault(r => r.File == item?.Item.Frame && item?.Kind == ItemKind.Token) ?? Rings.ElementAtOrDefault(1) ?? NoFrame;
        _background = Backgrounds.FirstOrDefault(b => b.File == item?.Item.Background) ?? NoFrame;
        _zoom = 1;
        _offsetX = 0;
        _offsetY = 0;
        _side = item?.Item.Side ?? "";
        OnEverythingChanged();
    }

    public string EditName
    {
        get => _editName;
        set
        {
            if (SetField(ref _editName, value ?? ""))
                RenameCommand.RaiseCanExecuteChanged();
        }
    }

    public string EditCaption
    {
        get => _editCaption;
        set
        {
            if (!SetField(ref _editCaption, value ?? "") || Selected is null)
                return;
            Selected.Item.Caption = _editCaption;
            SaveManifest();
        }
    }

    public bool GridEnabled
    {
        get => _gridEnabled;
        set
        {
            if (SetField(ref _gridEnabled, value))
            {
                ApplyGrid();
                OnPropertyChanged(nameof(GridText));
            }
        }
    }

    public double GridSize
    {
        get => _gridSize;
        set
        {
            if (SetField(ref _gridSize, Math.Max(1, value)))
                OnPropertyChanged(nameof(GridText));
        }
    }

    public double GridOffsetX
    {
        get => _gridOffsetX;
        set => SetField(ref _gridOffsetX, value);
    }

    public double GridOffsetY
    {
        get => _gridOffsetY;
        set => SetField(ref _gridOffsetY, value);
    }

    /// <summary>What the grid comes to, or that there is none, which is a choice and says so.</summary>
    public string GridText
    {
        get
        {
            if (Selected is not { IsMap: true } map)
                return "";
            if (!_gridEnabled)
                return _host.Text["kit.grid.none.long"];
            var (c, r) = Pictures.Squares(map.Item.Width - 2 * map.Item.Padding, map.Item.Height - 2 * map.Item.Padding,
                new GridSpec { Size = _gridSize, OffsetX = _gridOffsetX, OffsetY = _gridOffsetY });
            return _host.Text.Format("kit.grid.squares.long", c, r, (int)Math.Round(_gridSize));
        }
    }

    public FrameChoice Border
    {
        get => _border;
        set
        {
            if (SetField(ref _border, value ?? NoFrame))
                FrameCommand.RaiseCanExecuteChanged();
        }
    }

    public FrameChoice Ring
    {
        get => _ring;
        set
        {
            if (SetField(ref _ring, value ?? NoFrame))
                _ = ShowPreviewAsync();
        }
    }

    public FrameChoice Background
    {
        get => _background;
        set
        {
            if (SetField(ref _background, value ?? NoFrame))
                _ = ShowPreviewAsync();
        }
    }

    public double Zoom
    {
        get => _zoom;
        set
        {
            if (SetField(ref _zoom, Math.Clamp(value, 0.5, 4)))
                _ = ShowPreviewAsync();
        }
    }

    public double OffsetX
    {
        get => _offsetX;
        set
        {
            if (SetField(ref _offsetX, Math.Clamp(value, -1, 1)))
                _ = ShowPreviewAsync();
        }
    }

    public double OffsetY
    {
        get => _offsetY;
        set
        {
            if (SetField(ref _offsetY, Math.Clamp(value, -1, 1)))
                _ = ShowPreviewAsync();
        }
    }

    public string Side
    {
        get => _side;
        set
        {
            if (!SetField(ref _side, value ?? "") || Selected is null)
                return;
            Selected.Item.Side = _side;
            SaveManifest();
        }
    }

    public Bitmap? PreviewImage
    {
        get => _previewImage;
        private set
        {
            var old = _previewImage;
            if (SetField(ref _previewImage, value))
            {
                OnPropertyChanged(nameof(HasPreview));
                old?.Dispose();
            }
        }
    }

    public bool HasPreview => _previewImage is not null;

    /// <summary>
    /// The picture as it would be: a token cut and ringed with the sliders as they are, a map
    /// with its grid drawn over it, a handout as itself. Rendered off the UI thread, latest wins.
    /// </summary>
    private async Task ShowPreviewAsync()
    {
        _preview?.Cancel();
        var item = Selected;
        if (item is null)
        {
            PreviewImage = null;
            return;
        }

        var source = new CancellationTokenSource();
        _preview = source;
        var path = item.Path;
        var kind = item.Kind;
        var ring = _frames.Token(_ring.File);
        var background = _frames.Background(_background.File);
        var crop = new TokenCrop((float)_zoom, (float)_offsetX, (float)_offsetY);
        var grid = kind == ItemKind.Map && _gridEnabled ? new GridSpec { Size = _gridSize, OffsetX = _gridOffsetX, OffsetY = _gridOffsetY } : null;
        var padding = item.Item.Padding;

        try
        {
            var rendered = await Task.Run(() =>
            {
                using var bitmap = Preparer.Decode(SafeRead(path));
                if (bitmap is null)
                    return null;
                return kind == ItemKind.Token
                    ? Pictures.MakeToken(bitmap, ring, crop, 256, background)
                    : PreviewWithGrid(bitmap, grid, padding);
            }, source.Token);

            if (!source.IsCancellationRequested && ReferenceEquals(_preview, source))
                PreviewImage = Thumbnails.FromBytes(rendered, null);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not preview {path}: {ex.Message}");
        }
    }

    /// <summary>The map at preview size with the grid lines over it, so the eye can say whether the number is right.</summary>
    private static byte[] PreviewWithGrid(SKBitmap bitmap, GridSpec? grid, int padding)
    {
        var scale = Math.Min(1f, PreviewWidth / (float)bitmap.Width);
        var w = Math.Max(1, (int)(bitmap.Width * scale));
        var h = Math.Max(1, (int)(bitmap.Height * scale));
        using var surface = SKSurface.Create(new SKImageInfo(w, h));
        var canvas = surface.Canvas;
        using (var image = SKImage.FromBitmap(bitmap))
            canvas.DrawImage(image, new SKRect(0, 0, w, h), new SKSamplingOptions(SKFilterMode.Linear, SKMipmapMode.Linear));

        if (grid is { Enabled: true, Size: > 0 })
        {
            using var paint = new SKPaint { Color = new SKColor(0, 200, 255, 170), StrokeWidth = 1, IsStroke = true, IsAntialias = false };
            var step = (float)(grid.Size * scale);
            for (var x = (float)((grid.OffsetX + padding) * scale); x < w; x += step)
                canvas.DrawLine(x, 0, x, h, paint);
            for (var y = (float)((grid.OffsetY + padding) * scale); y < h; y += step)
                canvas.DrawLine(0, y, w, y, paint);
        }

        using var snapshot = surface.Snapshot();
        using var data = snapshot.Encode(SKEncodedImageFormat.Png, 100);
        return data.ToArray();
    }

    // ---- the verbs ----

    /// <summary>The name in the tab is the name in the VTT, and typing it renames the file.</summary>
    private void Rename()
    {
        if (Selected is not { } item || SelectedKit is not { } kit)
            return;

        var name = EditName.Trim();
        var stem = Exporter.Slug(name);
        var target = Path.Combine(Path.GetDirectoryName(item.Path)!, stem + Path.GetExtension(item.Path));

        if (!string.Equals(target, item.Path, StringComparison.OrdinalIgnoreCase))
        {
            if (File.Exists(target))
            {
                ErrorMessage = _host.Text.Format("kit.error.nameclash", Path.GetFileName(target));
                return;
            }
            try
            {
                File.Move(item.Path, target);
            }
            catch (Exception ex)
            {
                ErrorMessage = _host.Text.Format("kit.error.rename", ex.Message);
                return;
            }
            item.Item.File = Path.GetRelativePath(kit.Folder, target).Replace('\\', '/');
        }

        item.Item.Name = name;
        ErrorMessage = null;
        SaveManifest();
        item.Reread();
        Status = _host.Text.Format("kit.status.renamed", name);
    }

    private void GuessGrid()
    {
        if (Selected is not { IsMap: true } map)
            return;
        GridSize = Pictures.GuessGrid(map.Item.Width - 2 * map.Item.Padding, map.Item.Height - 2 * map.Item.Padding);
        GridOffsetX = 0;
        GridOffsetY = 0;
        ApplyGrid();
    }

    private void ApplyGrid()
    {
        if (Selected is not { IsMap: true } map)
            return;
        map.Item.Grid ??= new GridSpec();
        map.Item.Grid.Enabled = _gridEnabled;
        map.Item.Grid.Size = _gridSize;
        map.Item.Grid.OffsetX = _gridOffsetX;
        map.Item.Grid.OffsetY = _gridOffsetY;
        SaveManifest();
        map.Reread();
        OnPropertyChanged(nameof(GridText));
        _ = ShowPreviewAsync();
    }

    /// <summary>Down to the longest side chosen, as WebP, the grid scaled with it, the original kept.</summary>
    private async Task FitAsync()
    {
        if (Selected is not { } item || SelectedKit is not { } kit)
            return;

        IsBusy = true;
        Status = _host.Text.Format("kit.status.fitting", item.Name);
        try
        {
            var path = item.Path;
            var maxSide = FitMaxSide;
            var (bytes, w, h, before) = await Task.Run(() =>
            {
                var original = File.ReadAllBytes(path);
                using var bitmap = Preparer.Decode(original) ?? throw new InvalidDataException("could not read the picture");
                using var small = Math.Max(bitmap.Width, bitmap.Height) > maxSide ? Preparer.Shrink(bitmap, maxSide) : bitmap.Copy();
                var format = Preparer.HasTransparency(small) ? ImageFormat.Png : ImageFormat.WebP;
                return (Preparer.Encode(small, format, 88), small.Width, small.Height, original.LongLength);
            });

            var scale = (double)w / item.Item.Width;
            KeepOriginal(kit.Folder, path);
            var target = Path.ChangeExtension(path, bytes.Length > 0 && bytes[0] == 0x89 ? ".png" : ".webp");
            File.WriteAllBytes(target, bytes);
            if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
                File.Delete(path);

            item.Item.File = Path.GetRelativePath(kit.Folder, target).Replace('\\', '/');
            item.Item.Width = w;
            item.Item.Height = h;
            item.Item.Padding = (int)Math.Round(item.Item.Padding * scale);
            if (item.Item.Grid is { } grid)
            {
                grid.Size *= scale;
                grid.OffsetX *= scale;
                grid.OffsetY *= scale;
                _gridSize = grid.Size;
                _gridOffsetX = grid.OffsetX;
                _gridOffsetY = grid.OffsetY;
                OnPropertyChanged(nameof(GridSize));
                OnPropertyChanged(nameof(GridOffsetX));
                OnPropertyChanged(nameof(GridOffsetY));
                OnPropertyChanged(nameof(GridText));
            }
            SaveManifest();
            item.Reread();
            _ = item.LoadThumbnailAsync(ThumbnailWidth, CancellationToken.None);
            _ = ShowPreviewAsync();
            Status = _host.Text.Format("kit.status.fitted", item.Name, Humanise(before), Humanise(bytes.LongLength));
            _host.Store.Record("fitted", target, Status);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("kit.error.fit", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>A border round a map or a handout, outside the picture, the padding remembered.</summary>
    private async Task FrameAsync()
    {
        if (Selected is not { } item || SelectedKit is not { } kit || _frames.Border(Border.File) is not { } frame)
            return;

        IsBusy = true;
        try
        {
            var path = item.Path;
            var (png, padding, w, h) = await Task.Run(() =>
            {
                using var bitmap = Preparer.Decode(File.ReadAllBytes(path)) ?? throw new InvalidDataException("could not read the picture");
                var (bytes, pad) = Pictures.AddBorder(bitmap, frame, Math.Max(0.5f, bitmap.Width / 2000f));
                return (bytes, pad, bitmap.Width + 2 * pad, bitmap.Height + 2 * pad);
            });

            KeepOriginal(kit.Folder, path);
            var target = Path.ChangeExtension(path, ".png");
            File.WriteAllBytes(target, png);
            if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
                File.Delete(path);

            item.Item.File = Path.GetRelativePath(kit.Folder, target).Replace('\\', '/');
            item.Item.Width = w;
            item.Item.Height = h;
            item.Item.Padding += padding;
            item.Item.Frame = frame.File;
            SaveManifest();
            item.Reread();
            _ = item.LoadThumbnailAsync(ThumbnailWidth, CancellationToken.None);
            _ = ShowPreviewAsync();
            Status = _host.Text.Format("kit.status.framed", item.Name, frame.Label, padding);
            _host.Store.Record("framed", target, Status);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("kit.error.frame", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>The token as the preview shows it, written over the file, the original kept.</summary>
    private async Task MakeTokenAsync()
    {
        if (Selected is not { IsToken: true } item || SelectedKit is not { } kit)
            return;

        IsBusy = true;
        try
        {
            var path = item.Path;
            var ring = _frames.Token(Ring.File);
            var background = _frames.Background(Background.File);
            var crop = new TokenCrop((float)Zoom, (float)OffsetX, (float)OffsetY);
            var png = await Task.Run(() =>
            {
                using var bitmap = Preparer.Decode(File.ReadAllBytes(path)) ?? throw new InvalidDataException("could not read the picture");
                return Pictures.MakeToken(bitmap, ring, crop, 512, background);
            });

            KeepOriginal(kit.Folder, path);
            var target = Path.ChangeExtension(path, ".png");
            File.WriteAllBytes(target, png);
            if (!string.Equals(target, path, StringComparison.OrdinalIgnoreCase))
                File.Delete(path);

            item.Item.File = Path.GetRelativePath(kit.Folder, target).Replace('\\', '/');
            item.Item.Width = 512;
            item.Item.Height = 512;
            item.Item.Frame = ring?.File;
            item.Item.Background = background?.File;
            SaveManifest();
            item.Reread();
            _zoom = 1;
            _offsetX = 0;
            _offsetY = 0;
            OnPropertyChanged(nameof(Zoom));
            OnPropertyChanged(nameof(OffsetX));
            OnPropertyChanged(nameof(OffsetY));
            _ = item.LoadThumbnailAsync(ThumbnailWidth, CancellationToken.None);
            _ = ShowPreviewAsync();
            Status = _host.Text.Format("kit.status.token", item.Name, ring?.Label ?? _host.Text["kit.frame.none"]);
            _host.Store.Record("token", target, Status);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("kit.error.token", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    /// <summary>
    /// The picture before anything was done to it, under originals/ beside its folder, once: a
    /// second change keeps the first original, since that is the one worth going back to.
    /// </summary>
    private static void KeepOriginal(string kitFolder, string path)
    {
        var originals = Path.Combine(kitFolder, "originals");
        Directory.CreateDirectory(originals);
        var stem = Path.GetFileNameWithoutExtension(path);
        if (Directory.EnumerateFiles(originals, stem + ".*").Any())
            return;
        File.Copy(path, Path.Combine(originals, Path.GetFileName(path)));
    }

    private void Move(int delta)
    {
        if (Selected is not { } item || _manifest is null)
            return;
        var list = item.Kind switch { ItemKind.Map => _manifest.Maps, ItemKind.Token => _manifest.Tokens, _ => _manifest.Handouts };
        var index = list.IndexOf(item.Item);
        var target = index + delta;
        if (index < 0 || target < 0 || target >= list.Count)
            return;
        (list[index], list[target]) = (list[target], list[index]);
        SaveManifest();

        var viewIndex = Items.IndexOf(item);
        Items.Move(viewIndex, viewIndex + delta);
    }

    /// <summary>Out of the kit and into the Recycle Bin, never File.Delete; the original stays under originals/.</summary>
    private void Remove()
    {
        if (Selected is not { } item || _manifest is null || SelectedKit is not { } kit)
            return;

        var outcome = Disk.RecycleBin.Send([item.Path]);
        if (!outcome.Succeeded)
        {
            ErrorMessage = outcome.FailureReason ?? _host.Text["kit.error.remove"];
            return;
        }

        var name = item.Name;
        _manifest.Reconcile(kit.Folder);
        SaveManifest();
        LoadKit();
        Status = _host.Text.Format("kit.status.removed", name);
    }

    // ---- export ----

    private async Task ExportAsync(bool foundry)
    {
        if (_manifest is not { } manifest || SelectedKit is not { } kit)
            return;

        IsBusy = true;
        Status = _host.Text[foundry ? "kit.status.exporting.foundry" : "kit.status.exporting.roll20"];
        try
        {
            var folder = kit.Folder;
            var root = ExportRoot;
            var limit = Roll20MaxMegabytes * 1_000_000L;
            var report = await Task.Run(() => foundry
                ? Exporter.ForFoundry(folder, manifest, root)
                : Exporter.ForRoll20(folder, manifest, root, limit));

            Status = _host.Text.Format(foundry ? "kit.status.exported.foundry" : "kit.status.exported.roll20", report.Files, report.Folder);
            foreach (var note in report.Notes.Where(n => n != "foundry"))
                _host.Log(LogLevel.Warning, note);
            _host.Store.Record(foundry ? "exported-foundry" : "exported-roll20", report.Folder, Status);
            _host.Notifications.Post(NotificationSeverity.Info, _host.Text["kit.notify.exported"], Status,
                new NotificationAction(_host.Text["kit.notify.open"], () => Open(report.Folder), DismissesAfter: true));
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("kit.error.export", ex.Message);
        }
        finally
        {
            IsBusy = false;
        }
    }

    // ---- plumbing ----

    private void SaveManifest()
    {
        if (_manifest is null || SelectedKit is null)
            return;
        try
        {
            _manifest.Save(SelectedKit.Folder);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("kit.error.save", ex.Message);
        }
    }

    private void SaveSettings()
    {
        try
        {
            _host.SaveSettings(_settings);
        }
        catch (Exception ex)
        {
            _host.Log(LogLevel.Warning, $"Could not save Kit settings: {ex.Message}");
        }
    }

    private void RaiseCommands()
    {
        foreach (var command in new[]
                 {
                     RenameCommand, GuessGridCommand, ApplyGridCommand, FitCommand, FrameCommand, MakeTokenCommand,
                     MoveUpCommand, MoveDownCommand, RemoveCommand, ExportFoundryCommand, ExportRoll20Command,
                 })
            command.RaiseCanExecuteChanged();
    }

    private void Open(string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder))
            return;
        try
        {
            Directory.CreateDirectory(folder);
            Explorer.Open(folder);
        }
        catch (Exception ex)
        {
            ErrorMessage = _host.Text.Format("kit.error.open", folder, ex.Message);
        }
    }

    private static string Unique(string path)
    {
        if (!File.Exists(path))
            return path;
        var stem = Path.GetFileNameWithoutExtension(path);
        var ext = Path.GetExtension(path);
        var folder = Path.GetDirectoryName(path)!;
        for (var i = 2; ; i++)
        {
            var candidate = Path.Combine(folder, $"{stem} ({i}){ext}");
            if (!File.Exists(candidate))
                return candidate;
        }
    }

    private static string Humanise(long bytes) => bytes switch
    {
        >= 1024 * 1024 => $"{bytes / 1024d / 1024:0.#} MB",
        >= 1024 => $"{bytes / 1024d:0} KB",
        _ => $"{bytes} B",
    };

    /// <summary>Ctrl+K reaching into the open kit: a picture by name, or a kit by its folder name.</summary>
    public IReadOnlyList<SearchHit> Search(string query, int limit)
    {
        var words = SearchWords.Split(query);
        var hits = new List<SearchHit>();

        foreach (var item in Items)
        {
            if (hits.Count >= limit)
                break;
            if (!SearchWords.Match(words, item.Name, item.FileName))
                continue;
            var chosen = item;
            hits.Add(new SearchHit(item.Name, $"{KitTitle} · {item.Detail}", () => Selected = chosen));
        }

        foreach (var encounter in Encounters)
        {
            if (hits.Count >= limit)
                break;
            if (!SearchWords.Match(words, encounter.Name, encounter.Roster))
                continue;
            var chosen = encounter;
            hits.Add(new SearchHit(encounter.Title, $"{KitTitle} · {encounter.Detail}", () =>
            {
                ShowRunSheet = true;
                SelectedEncounter = chosen;
            }));
        }

        foreach (var kit in Kits)
        {
            if (hits.Count >= limit)
                break;
            if (ReferenceEquals(kit, SelectedKit) || !SearchWords.Match(words, kit.Name))
                continue;
            var chosen = kit;
            hits.Add(new SearchHit(kit.Name, _host.Text["kit.search.kit"], () => SelectedKit = chosen));
        }

        return hits;
    }

    public void Dispose()
    {
        SaveNote();
        _thumbnails?.Cancel();
        _preview?.Cancel();
        foreach (var item in Items)
            item.Dispose();
        PreviewImage = null;
        _language.Dispose();
    }
}
