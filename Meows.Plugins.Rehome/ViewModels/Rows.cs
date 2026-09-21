using Meows.Disk;
using Meows.Plugins.Abstractions;
using Meows.Plugins.Rehome.Services;

namespace Meows.Plugins.Rehome.ViewModels;

/// <summary>One installed program and the way to get it back.</summary>
public sealed class ProgramRowViewModel(ProgramMatch match) : ObservableObject
{
    private bool _isWanted;

    public ProgramMatch Match { get; } = match;

    public event Action<ProgramRowViewModel>? WantedChanged;

    /// <summary>Ticked for the to-get list: the short list of what the person wants back.</summary>
    public bool IsWanted
    {
        get => _isWanted;
        set
        {
            if (SetField(ref _isWanted, value))
                WantedChanged?.Invoke(this);
        }
    }

    /// <summary>Sets the tick without announcing it, for a list being rebuilt.</summary>
    public void Wanted(bool value) => _isWanted = value;

    public string Name => Match.Program.Name;

    public string Version => Match.Program.Version ?? "";

    public string Publisher => Match.Program.Publisher ?? "";

    public string? Url => Match.Program.Url;

    public bool ByHand => Match.ByHand;

    public bool IsLikely => Match.Confidence == MatchConfidence.Likely;

    /// <summary>"winget · 7zip.7zip", "Chocolatey · vlc", or "by hand".</summary>
    public string HowText => Match.Manager switch
    {
        Manager.Winget => $"winget · {Match.Id}",
        Manager.Chocolatey => $"Chocolatey · {Match.Id}",
        Manager.Scoop => $"Scoop · {Match.Id}",
        Manager.Launcher => MeowsText.Current.Format("rehome.how.launcher", Match.Id),
        _ => MeowsText.Current["rehome.how.byhand"],
    };

    public string SureText => Match.Confidence switch
    {
        _ when Match.Manager == Manager.Launcher => "",
        MatchConfidence.Exact => MeowsText.Current["rehome.sure.exact"],
        MatchConfidence.Likely => MeowsText.Current["rehome.sure.likely"],
        _ => "",
    };

    public string InstalledText => Match.Program.InstalledOn?.ToString("d MMM yyyy") ?? "";

    public string Where => Match.Program.Location ?? "";

    /// <summary>"on F:" for a program whose files the wipe leaves alone; blank when it goes.</summary>
    public string DriveText => Match.Program.Drive is { } drive && !OnWipedDrive ? MeowsText.Current.Format("rehome.program.elsewhere", drive) : "";

    public bool OnWipedDrive { get; set; } = true;

    public void Reread() => OnEverythingChanged();
}

/// <summary>One folder the wipe would take, with a tick to carry it.</summary>
public sealed class FolderRowViewModel : ObservableObject
{
    private bool _isPicked;
    private MeasuredFolder? _measured;

    public FolderRowViewModel(WipeCandidate candidate, bool picked)
    {
        Candidate = candidate;
        _isPicked = picked;
    }

    public WipeCandidate Candidate { get; }

    public event Action? PickedChanged;

    public bool IsPicked
    {
        get => _isPicked;
        set
        {
            if (SetField(ref _isPicked, value))
                PickedChanged?.Invoke();
        }
    }

    public MeasuredFolder? Measured
    {
        get => _measured;
        set
        {
            _measured = value;
            OnEverythingChanged();
        }
    }

    public string Name => Candidate.Name;

    public string Path => Candidate.Path;

    public bool IsBrowser => Candidate.Kind == FolderKind.Browser;

    /// <summary>In the group at the top: the person's own folders, and OneDrive beside them.</summary>
    public bool IsLibrary => Candidate.Kind is FolderKind.Library or FolderKind.Cloud;

    public bool IsCloud => Candidate.Kind == FolderKind.Cloud;

    public bool Survives => Candidate.Survives;

    /// <summary>Whether cloud-only files count, set by the tab from the one option; the size and the detail follow it.</summary>
    public bool IncludeCloud { get; set; }

    public bool IsCache => Candidate.Kind == FolderKind.Cache;

    public string? Note => Candidate.Note;

    public bool HasNote => !string.IsNullOrEmpty(Candidate.Note);

    public long Bytes => _measured is null ? 0 : _measured.Bytes + (IncludeCloud ? _measured.CloudBytes : 0);

    /// <summary>Cloud-only files, which the copy skips unless told to pull them down.</summary>
    public int CloudOnly => _measured?.Skipped ?? 0;

    public string KindText => MeowsText.Current[Candidate.Kind switch
    {
        FolderKind.Library => "rehome.kind.library",
        FolderKind.Cloud => "rehome.kind.cloud",
        FolderKind.Profile => "rehome.kind.profile",
        FolderKind.Roaming => "rehome.kind.roaming",
        FolderKind.Local => "rehome.kind.local",
        FolderKind.LocalLow => "rehome.kind.locallow",
        FolderKind.ProgramData => "rehome.kind.programdata",
        FolderKind.Root => "rehome.kind.root",
        FolderKind.Browser => "rehome.kind.browser",
        FolderKind.Saves => "rehome.kind.saves",
        _ => "rehome.kind.cache",
    }];

    public string SizeText => _measured is null ? "…" : FolderSize.Humanise(Bytes);

    /// <summary>"1,204 files, newest 3 Sep 2026" with the skips when there were any.</summary>
    public string DetailText
    {
        get
        {
            if (_measured is null)
                return MeowsText.Current["rehome.measuring"];
            var text = MeowsText.Current.Format("rehome.folder.detail", _measured.Files, _measured.Newest?.ToString("d MMM yyyy") ?? "–");
            if (_measured.Skipped > 0)
                text += MeowsText.Current.Format(IncludeCloud ? "rehome.folder.pulled" : "rehome.folder.skipped", _measured.Skipped, FolderSize.Humanise(_measured.CloudBytes));
            if (_measured.Unreadable > 0)
                text += MeowsText.Current.Format("rehome.folder.unreadable", _measured.Unreadable);
            return text;
        }
    }

    public void Reread() => OnEverythingChanged();
}

/// <summary>One of the not-folders.</summary>
public sealed class ExtraRowViewModel : ObservableObject
{
    private bool _isPicked;

    public ExtraRowViewModel(ExtraItem item)
    {
        Item = item;
        _isPicked = item.Present;
    }

    public ExtraItem Item { get; }

    public event Action? PickedChanged;

    public bool IsPicked
    {
        get => _isPicked;
        set
        {
            if (SetField(ref _isPicked, value))
                PickedChanged?.Invoke();
        }
    }

    public bool Present => Item.Present;

    public string Name => MeowsText.Current[$"rehome.extra.{Item.Kind.ToString().ToLowerInvariant()}"];

    public string Note => MeowsText.Current[$"rehome.extra.{Item.Kind.ToString().ToLowerInvariant()}.note"];

    public string Detail => Item.Present ? Item.Detail : MeowsText.Current["rehome.extra.absent"];

    public string Source => Item.Source ?? "";

    public void Reread() => OnEverythingChanged();
}

/// <summary>One licence row.</summary>
public sealed class KeyRowViewModel(LicenceRow row) : ObservableObject
{
    public LicenceRow Row { get; } = row;

    public string Product => Row.Product;

    public string Key => Row.Key ?? "";

    public bool HasKey => Row.Key is not null;

    public string Detail => Row.Detail;

    public bool IsWarning => Row.Standing is KeyStanding.Digital or KeyStanding.NotFound;

    public string StandingText => MeowsText.Current[Row.Standing switch
    {
        KeyStanding.Key => "rehome.standing.key",
        KeyStanding.Digital => "rehome.standing.digital",
        KeyStanding.Account => "rehome.standing.account",
        KeyStanding.Partial => "rehome.standing.partial",
        _ => "rehome.standing.notfound",
    }];

    public void Reread() => OnEverythingChanged();
}

/// <summary>A drive on the machine, and whether the reinstall takes it.</summary>
public sealed class DriveRowViewModel : ObservableObject
{
    private bool _isWiped;

    public DriveRowViewModel(DriveInfo drive, bool isWiped, bool isSystem)
    {
        Root = drive.Name.TrimEnd('\\');
        IsSystem = isSystem;
        _isWiped = isWiped;
        string label;
        long free = 0, total = 0;
        try
        {
            label = drive.VolumeLabel;
            free = drive.AvailableFreeSpace;
            total = drive.TotalSize;
        }
        catch (Exception)
        {
            label = "";
        }
        Label = label;
        Free = free;
        Total = total;
    }

    public string Root { get; }

    public string Label { get; }

    public long Free { get; }

    public long Total { get; }

    public bool IsSystem { get; }

    public event Action? WipedChanged;

    public bool IsWiped
    {
        get => _isWiped;
        set
        {
            if (SetField(ref _isWiped, value))
                WipedChanged?.Invoke();
        }
    }

    /// <summary>"C: Windows (system)" or "F: Data".</summary>
    public string Name => IsSystem ? MeowsText.Current.Format("rehome.drive.system", Root, Label) : $"{Root} {Label}".Trim();

    public string FreeText => MeowsText.Current.Format("rehome.drive.free", FolderSize.Humanise(Free), FolderSize.Humanise(Total));

    public void Reread() => OnEverythingChanged();
}

/// <summary>How a folder that already exists at the destination is treated on the way back.</summary>
public enum Conflict { KeepMine, KeepTheirs, KeepBoth }

public sealed record ConflictOption(Conflict Value, string Key)
{
    public string Text => MeowsText.Current[Key];
}

/// <summary>One folder in the manifest on the way back.</summary>
public sealed class RestoreRowViewModel : ObservableObject
{
    private bool _isPicked;
    private ConflictOption _conflict;
    private string? _outcome;
    private bool _failed;

    public RestoreRowViewModel(ManifestEntry entry, string root, IReadOnlyList<ConflictOption> options)
    {
        Entry = entry;
        Stored = System.IO.Path.Combine(root, entry.Stored);
        Exists = Directory.Exists(entry.Source);
        HasCopy = Directory.Exists(Stored);
        _isPicked = HasCopy && !Exists;
        Options = options;
        _conflict = options[0];
        DateTime? newestHere = null;
        if (Exists)
        {
            // The first few thousand files are enough to date what is there; this runs on the UI
            // thread when the manifest opens, and a Documents folder can hold a million.
            try
            {
                var walk = new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint };
                newestHere = new DirectoryInfo(entry.Source).EnumerateFiles("*", walk).Take(5000).Select(f => (DateTime?)f.LastWriteTime).DefaultIfEmpty(null).Max();
            }
            catch (Exception)
            {
                // Half-readable is still "exists".
            }
        }
        NewestHere = newestHere;
    }

    public ManifestEntry Entry { get; }

    public string Stored { get; }

    public bool Exists { get; }

    public bool HasCopy { get; }

    public DateTime? NewestHere { get; }

    public IReadOnlyList<ConflictOption> Options { get; }

    public event Action? PickedChanged;

    public bool IsPicked
    {
        get => _isPicked;
        set
        {
            if (SetField(ref _isPicked, value))
                PickedChanged?.Invoke();
        }
    }

    public ConflictOption Conflict
    {
        get => _conflict;
        set => SetField(ref _conflict, value);
    }

    public string Name => System.IO.Path.GetFileName(Entry.Source.TrimEnd('\\'));

    public string Source => Entry.Source;

    public string SizeText => FolderSize.Humanise(Entry.Bytes);

    public string Carried => MeowsText.Current.Format("rehome.back.carried", Entry.Files, Entry.Newest?.ToString("d MMM yyyy") ?? "–");

    public bool WasIncomplete => !Entry.Complete;

    /// <summary>"nothing there yet", or "already there, newest 5 Sep 2026" so the choice is informed.</summary>
    public string HereText => !HasCopy
        ? MeowsText.Current["rehome.back.nocopy"]
        : Exists
            ? MeowsText.Current.Format("rehome.back.exists", NewestHere?.ToString("d MMM yyyy") ?? "–")
            : MeowsText.Current["rehome.back.missing"];

    public string? Outcome
    {
        get => _outcome;
        set
        {
            if (SetField(ref _outcome, value))
                OnPropertyChanged(nameof(HasOutcome));
        }
    }

    public bool HasOutcome => _outcome is not null;

    public bool Failed
    {
        get => _failed;
        set => SetField(ref _failed, value);
    }

    public void Reread() => OnEverythingChanged();
}

/// <summary>One extra in the manifest on the way back.</summary>
public sealed class RestoreExtraViewModel : ObservableObject
{
    private bool _isPicked;
    private string? _outcome;
    private bool _failed;

    public RestoreExtraViewModel(ManifestExtra extra, string root)
    {
        Extra = extra;
        Stored = System.IO.Path.Combine(root, extra.Stored);
        Kind = Enum.TryParse<ExtraKind>(extra.Kind, out var kind) ? kind : null;
        HasCopy = extra.Ok && (File.Exists(Stored) || Directory.Exists(Stored));
        CanPutBack = Kind is ExtraKind.Wifi or ExtraKind.GitConfig or ExtraKind.PowerShellProfile or ExtraKind.Fonts;
        _isPicked = HasCopy && CanPutBack;
    }

    public ManifestExtra Extra { get; }

    public ExtraKind? Kind { get; }

    public string Stored { get; }

    public bool HasCopy { get; }

    /// <summary>Wi-Fi has a verb, the config files can be copied back; the PATH and hosts are shown, not written.</summary>
    public bool CanPutBack { get; }

    public event Action? PickedChanged;

    public bool IsPicked
    {
        get => _isPicked;
        set
        {
            if (SetField(ref _isPicked, value))
                PickedChanged?.Invoke();
        }
    }

    public string Name => Kind is null ? Extra.Kind : MeowsText.Current[$"rehome.extra.{Extra.Kind.ToLowerInvariant()}"];

    public string Detail => !HasCopy
        ? MeowsText.Current["rehome.back.nocopy"]
        : CanPutBack
            ? MeowsText.Current[$"rehome.back.extra.{Extra.Kind.ToLowerInvariant()}"]
            : MeowsText.Current["rehome.back.extra.byhand"];

    public string? Outcome
    {
        get => _outcome;
        set
        {
            if (SetField(ref _outcome, value))
                OnPropertyChanged(nameof(HasOutcome));
        }
    }

    public bool HasOutcome => _outcome is not null;

    public bool Failed
    {
        get => _failed;
        set => SetField(ref _failed, value);
    }

    public void Reread() => OnEverythingChanged();
}
