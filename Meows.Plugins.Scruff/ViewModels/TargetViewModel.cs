using System.Collections.ObjectModel;
using Meows.Plugins.Abstractions;
using Meows.Media;
using Meows.Plugins.Scruff.Services;

namespace Meows.Plugins.Scruff.ViewModels;

/// <summary>One line on a hand-off sheet, with what to paste.</summary>
public sealed class SheetFieldViewModel(string labelKey, string value)
{
    public string LabelKey { get; } = labelKey;

    public string Label => MeowsText.Current[LabelKey];

    public string Value { get; } = value;

    public bool HasValue => Value.Length > 0;
}

/// <summary>
/// One place on the right hand side: whether it is in, who it is signed in as, what it would be
/// sent, and how that went.
/// </summary>
public sealed class TargetViewModel : ObservableObject
{
    private readonly Action _changed;
    private bool _isEnabled;
    private string _preview = "";
    private string _lengthText = "";
    private bool _isOverLimit;
    private bool _canGo = true;
    private string _problemsText = "";
    private string _notesText = "";
    private string? _outcome;
    private string? _outcomeUrl;
    private bool _failed;
    private bool _isBusy;
    private string _loginA = "";
    private string _loginB = "";
    private string? _account;
    private string? _handoffFolder;
    private string? _handoffUrl;

    public TargetViewModel(IPostTarget target, bool enabled, Action changed)
    {
        Target = target;
        _isEnabled = enabled;
        _changed = changed;
        _account = (target as IApiTarget)?.Account;
    }

    public IPostTarget Target { get; }

    public string Id => Target.Id;

    public string Name => Target.Name;

    public bool PostsItself => Target.PostsItself;

    public bool IsBluesky => Target is BlueskyTarget;

    public bool IsMastodon => Target is MastodonTarget;

    public bool IsReddit => Target is RedditTarget;

    public bool IsDiscord => Target is DiscordTarget;

    /// <summary>DeviantArt and Tumblr: an app id and secret, and the browser does the rest.</summary>
    public bool IsOAuth => Target is OAuthTarget;

    public bool IsTumblr => Target is TumblrTarget;

    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetField(ref _isEnabled, value))
                return;
            _changed();
        }
    }

    /// <summary>Whether the place can be posted to as things stand: switched on, and signed in if it needs to be.</summary>
    public bool IsReady => _isEnabled && (Target is not IApiTarget api || api.IsSignedIn);

    public bool IsSignedIn => Target is IApiTarget { IsSignedIn: true };

    public bool NeedsSignIn => Target is IApiTarget { IsSignedIn: false };

    public string? Account
    {
        get => _account;
        set
        {
            SetField(ref _account, value);
            OnPropertyChanged(nameof(IsSignedIn));
            OnPropertyChanged(nameof(NeedsSignIn));
            OnPropertyChanged(nameof(IsReady));
        }
    }

    /// <summary>Handle or server, depending on the place. Typed, verified, then kept sealed.</summary>
    public string LoginA
    {
        get => _loginA;
        set => SetField(ref _loginA, value ?? "");
    }

    /// <summary>App password or token. Cleared from here the moment it is saved.</summary>
    public string LoginB
    {
        get => _loginB;
        set => SetField(ref _loginB, value ?? "");
    }

    public string Preview
    {
        get => _preview;
        private set => SetField(ref _preview, value);
    }

    public bool HasPreview => _preview.Length > 0;

    public string LengthText
    {
        get => _lengthText;
        private set => SetField(ref _lengthText, value);
    }

    public bool HasLength => _lengthText.Length > 0;

    public bool IsOverLimit
    {
        get => _isOverLimit;
        private set => SetField(ref _isOverLimit, value);
    }

    public bool CanGo
    {
        get => _canGo;
        private set => SetField(ref _canGo, value);
    }

    public string ProblemsText
    {
        get => _problemsText;
        private set
        {
            if (SetField(ref _problemsText, value))
                OnPropertyChanged(nameof(HasProblems));
        }
    }

    public bool HasProblems => _problemsText.Length > 0;

    public string NotesText
    {
        get => _notesText;
        private set
        {
            if (SetField(ref _notesText, value))
                OnPropertyChanged(nameof(HasNotes));
        }
    }

    public bool HasNotes => _notesText.Length > 0;

    /// <summary>What happened last time, as a line. Null until something has.</summary>
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

    public string? OutcomeUrl
    {
        get => _outcomeUrl;
        set
        {
            if (SetField(ref _outcomeUrl, value))
                OnPropertyChanged(nameof(HasOutcomeUrl));
        }
    }

    public bool HasOutcomeUrl => !string.IsNullOrEmpty(_outcomeUrl);

    public bool Failed
    {
        get => _failed;
        set => SetField(ref _failed, value);
    }

    public bool IsBusy
    {
        get => _isBusy;
        set => SetField(ref _isBusy, value);
    }

    // ---- The sheet, for a place done by hand ---------------------------------------------------

    public ObservableCollection<SheetFieldViewModel> Sheet { get; } = [];

    public ObservableCollection<string> Reminders { get; } = [];

    public bool HasSheet => Sheet.Count > 0;

    public bool HasReminders => Reminders.Count > 0;

    public string? HandoffFolder
    {
        get => _handoffFolder;
        set
        {
            if (SetField(ref _handoffFolder, value))
                OnPropertyChanged(nameof(HasHandoff));
        }
    }

    public string? HandoffUrl
    {
        get => _handoffUrl;
        set => SetField(ref _handoffUrl, value);
    }

    public bool HasHandoff => _handoffFolder is not null;

    public void ShowSheet(HandoffSheet sheet, IMeowsText text)
    {
        Sheet.Clear();
        foreach (var field in sheet.Fields)
            Sheet.Add(new SheetFieldViewModel(field.LabelKey, field.Value));

        Reminders.Clear();
        foreach (var reminder in sheet.Reminders)
            Reminders.Add(text.Format(reminder.Key, reminder.Values));

        HandoffUrl = sheet.Url;
        OnPropertyChanged(nameof(HasSheet));
        OnPropertyChanged(nameof(HasReminders));
    }

    public void ClearOutcome()
    {
        Outcome = null;
        OutcomeUrl = null;
        Failed = false;
        Sheet.Clear();
        Reminders.Clear();
        HandoffFolder = null;
        HandoffUrl = null;
        OnPropertyChanged(nameof(HasSheet));
        OnPropertyChanged(nameof(HasReminders));
    }

    /// <summary>Works out what this place would be sent now, and whether it can be.</summary>
    public void Recompose(Draft draft, IReadOnlyList<Outgoing> images, IMeowsText text)
    {
        var composed = Target.Compose(draft, images);

        Preview = composed.Text;
        OnPropertyChanged(nameof(HasPreview));

        LengthText = composed.Limit > 0 ? $"{composed.Length} / {composed.Limit}" : "";
        OnPropertyChanged(nameof(HasLength));

        IsOverLimit = composed.Limit > 0 && composed.Length > composed.Limit;
        CanGo = composed.CanGo;
        ProblemsText = string.Join("\n", composed.Problems.Select(p => text.Format(p.Key, p.Values)));
        NotesText = string.Join("\n", composed.Notes.Select(p => text.Format(p.Key, p.Values)));
    }

    /// <summary>Everything worked out in code reads differently now.</summary>
    public void Reread() => OnEverythingChanged();
}
