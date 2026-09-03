using Meows.Plugins.Abstractions;
using Meows.Plugins.TelegramPoster.Model;
using Meows.Plugins.TelegramPoster.Services;
using Meows.Bot;

namespace Meows.Plugins.TelegramPoster.ViewModels;

/// <summary>
/// One group from config.json, plus whatever its folders currently hold. Edits stay here
/// until Save, so a stray keystroke never reaches a running bot.
/// </summary>
public sealed class GroupViewModel : ObservableObject
{
    public static readonly string[] PostOrders = ["oldest", "newest", "random"];
    public static readonly string[] ComicOrders = ["name", "date", "zip_order"];

    private readonly BotWorkspace _workspace;
    private readonly Action _onDirtyChanged;

    private GroupConfig _saved;
    private string _name;
    private string _chatId;
    private string _folder;
    private bool _useInterval;
    private int _intervalMinutes;
    private int _hour;
    private int _minute;
    private int _jitterMinutes;
    private int _filesPerPost;
    private string _postOrder;
    private string _comicOrder;
    private bool _isEnabled;
    private bool _stretchEnabled;
    private double _stretchTargetDays;
    private int _stretchCapMinutes;
    private bool _isDirty;
    private IReadOnlyList<GroupIssue> _issues = [];
    private int _queueCount;
    private int _archiveCount;

    public GroupViewModel(GroupConfig config, BotWorkspace workspace, Action onDirtyChanged)
    {
        _workspace = workspace;
        _onDirtyChanged = onDirtyChanged;
        _saved = config.Clone();

        _name = config.Name;
        _chatId = config.ChatId;
        _folder = config.Folder;
        _useInterval = config.Schedule.IntervalMinutes is > 0;
        _intervalMinutes = config.Schedule.IntervalMinutes ?? 60;
        _hour = config.Schedule.Hour ?? 12;
        _minute = config.Schedule.Minute ?? 0;
        _jitterMinutes = config.JitterMinutes ?? 15;
        _filesPerPost = config.FilesPerPost ?? 1;
        _postOrder = Normalize(config.PostOrder, PostOrders, "oldest");
        _comicOrder = Normalize(config.ComicOrder, ComicOrders, "name");
        _isEnabled = config.Enabled ?? true;
        _stretchEnabled = config.Stretch?.TargetDays is > 0;
        _stretchTargetDays = config.Stretch?.TargetDays ?? 7;
        _stretchCapMinutes = config.Stretch?.MaxIntervalMinutes ?? QueueRunway.DefaultCapMinutes;

        RefreshCounts();
    }

    public string Name
    {
        get => _name;
        set => SetEdited(ref _name, value);
    }

    public string ChatId
    {
        get => _chatId;
        set => SetEdited(ref _chatId, value);
    }

    public string Folder
    {
        get => _folder;
        set
        {
            if (SetEdited(ref _folder, value))
            {
                OnPropertyChanged(nameof(ResolvedFolder));
                RefreshCounts();
            }
        }
    }

    public bool UseInterval
    {
        get => _useInterval;
        set
        {
            if (SetEdited(ref _useInterval, value))
                OnPropertyChanged(nameof(ScheduleSummary));
        }
    }

    public int IntervalMinutes
    {
        get => _intervalMinutes;
        set
        {
            if (SetEdited(ref _intervalMinutes, Math.Max(1, value)))
                OnPropertyChanged(nameof(ScheduleSummary));
        }
    }

    public int Hour
    {
        get => _hour;
        set
        {
            if (SetEdited(ref _hour, Math.Clamp(value, 0, 23)))
                OnPropertyChanged(nameof(ScheduleSummary));
        }
    }

    public int Minute
    {
        get => _minute;
        set
        {
            if (SetEdited(ref _minute, Math.Clamp(value, 0, 59)))
                OnPropertyChanged(nameof(ScheduleSummary));
        }
    }

    public int JitterMinutes
    {
        get => _jitterMinutes;
        set
        {
            if (SetEdited(ref _jitterMinutes, Math.Max(0, value)))
                OnPropertyChanged(nameof(ScheduleSummary));
        }
    }

    public int FilesPerPost
    {
        get => _filesPerPost;
        set => SetEdited(ref _filesPerPost, Math.Max(1, value));
    }

    public string PostOrder
    {
        get => _postOrder;
        set => SetEdited(ref _postOrder, value);
    }

    public string ComicOrder
    {
        get => _comicOrder;
        set => SetEdited(ref _comicOrder, value);
    }

    /// <summary>Unticking keeps the group in config.json but drops it from the schedule.</summary>
    public bool IsEnabled
    {
        get => _isEnabled;
        set
        {
            if (!SetEdited(ref _isEnabled, value))
                return;
            OnPropertyChanged(nameof(NextPostText));
            OnPropertyChanged(nameof(RowOpacity));
        }
    }

    /// <summary>Dim the disabled ones. Still readable, clearly out of play.</summary>
    public double RowOpacity => _isEnabled ? 1.0 : 0.45;

    /// <summary>Set from outside, since only the parent can see the other groups.</summary>
    public IReadOnlyList<GroupIssue> Issues
    {
        get => _issues;
        private set
        {
            _issues = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(HasIssues));
            OnPropertyChanged(nameof(HasErrors));
            OnPropertyChanged(nameof(HasWarningsOnly));
            OnPropertyChanged(nameof(IssueTooltip));
        }
    }

    public bool HasIssues => _issues.Count > 0;

    public bool HasErrors => _issues.Any(i => i.IsError);

    public bool HasWarningsOnly => HasIssues && !HasErrors;

    public string IssueTooltip => string.Join(Environment.NewLine, _issues.Select(i => "• " + i.Message));

    public void SetIssues(IReadOnlyList<GroupIssue> issues)
    {
        // Only swap if something changed. Otherwise validation loops through the
        // PropertyChanged that triggered it.
        if (_issues.Count == issues.Count && _issues.SequenceEqual(issues))
            return;
        Issues = issues;
    }

    public IReadOnlyList<string> PostOrderOptions => PostOrders;

    public IReadOnlyList<string> ComicOrderOptions => ComicOrders;

    public bool IsDirty
    {
        get => _isDirty;
        private set
        {
            if (SetField(ref _isDirty, value))
                _onDirtyChanged();
        }
    }

    public int QueueCount
    {
        get => _queueCount;
        private set
        {
            if (SetField(ref _queueCount, value))
                OnPropertyChanged(nameof(QueueSummary));
        }
    }

    public int ArchiveCount
    {
        get => _archiveCount;
        private set
        {
            if (SetField(ref _archiveCount, value))
                OnPropertyChanged(nameof(QueueSummary));
        }
    }

    public string QueueSummary => QueueCount == 0
        ? MeowsText.Current.Format("tp.queue.empty", ArchiveCount)
        : MeowsText.Current.Format("tp.queue.some", QueueCount, ArchiveCount);

    /// <summary>Empty queue, so the bot starts re-posting the archive.</summary>
    public bool IsStarving => QueueCount == 0;

    public string ResolvedFolder => _workspace.GroupFolder(ToConfig());

    public string ToSendFolder => _workspace.ToSendFolder(ToConfig());

    public string AlreadySentFolder => _workspace.AlreadySentFolder(ToConfig());

    public string ScheduleSummary => UseInterval
        ? MeowsText.Current.Format("tp.schedule.interval", IntervalMinutes, JitterMinutes)
        : MeowsText.Current.Format("tp.schedule.daily", $"{Hour:00}:{Minute:00}", JitterMinutes);

    /// <summary>
    /// Daily groups get a real time. Interval groups count from whenever the bot started,
    /// which we have no way of knowing, so say that rather than make something up.
    /// </summary>
    public string NextPostText
    {
        get
        {
            if (!IsEnabled)
                return MeowsText.Current["tp.next.disabled"];

            if (UseInterval)
                return MeowsText.Current.Format("tp.next.interval", IntervalMinutes);

            var now = DateTime.Now;
            var today = new DateTime(now.Year, now.Month, now.Day, Hour, Minute, 0);
            var next = today > now ? today : today.AddDays(1);
            var key = next.Date == now.Date ? "tp.next.today" : "tp.next.tomorrow";
            return MeowsText.Current.Format(key, next.ToString("HH:mm"), JitterMinutes);
        }
    }

    /// <summary>
    /// Whether the bot should slow this group down when its queue runs short. Off leaves it
    /// posting at its configured rate until there is nothing left, which is when it starts
    /// repeating the archive.
    /// </summary>
    public bool StretchEnabled
    {
        get => _stretchEnabled;
        set
        {
            if (SetEdited(ref _stretchEnabled, value))
                RaiseStretch();
        }
    }

    /// <summary>How long what is left should be made to last.</summary>
    public double StretchTargetDays
    {
        get => _stretchTargetDays;
        set
        {
            if (SetEdited(ref _stretchTargetDays, Math.Clamp(value, 0.5, 365)))
                RaiseStretch();
        }
    }

    /// <summary>As far apart as the posts are ever allowed to get.</summary>
    public int StretchCapMinutes
    {
        get => _stretchCapMinutes;
        set
        {
            if (SetEdited(ref _stretchCapMinutes, Math.Max(1, value)))
                RaiseStretch();
        }
    }

    /// <summary>
    /// What the bot is doing about this group right now, in words.
    ///
    /// Worked out from the same sum bot.py uses, because the pace on screen should be the pace
    /// it is running at rather than the one written in config.json.
    /// </summary>
    public string StretchSummary
    {
        get
        {
            if (!_stretchEnabled)
                return MeowsText.Current["tp.stretch.off"];

            if (!_useInterval)
                return MeowsText.Current["tp.stretch.daily"];

            var config = ToConfig();
            if (QueueRunway.StretchedIntervalMinutes(config, QueueCount) is not { } effective)
                return MeowsText.Current["tp.stretch.off"];

            if (effective <= _intervalMinutes)
                return MeowsText.Current.Format("tp.stretch.resting", _intervalMinutes);

            var reached = QueueRunway.Days(config, QueueCount) ?? 0;
            var key = effective >= _stretchCapMinutes
                ? "tp.stretch.capped"
                : "tp.stretch.active";

            return MeowsText.Current.Format(key, Describe(effective), reached.ToString("0.#"));
        }
    }

    /// <summary>Minutes as something readable, since a stretched gap is usually hours.</summary>
    private static string Describe(int minutes) => minutes < 120
        ? MeowsText.Current.Format("tp.stretch.minutes", minutes)
        : MeowsText.Current.Format("tp.stretch.hours", (minutes / 60d).ToString("0.#"));

    private void RaiseStretch()
    {
        OnPropertyChanged(nameof(StretchSummary));
        OnPropertyChanged(nameof(NextPostText));
    }

    public void RefreshCounts()
    {
        var config = ToConfig();
        QueueCount = _workspace.Scan(_workspace.ToSendFolder(config)).Count;
        ArchiveCount = _workspace.Scan(_workspace.AlreadySentFolder(config), recursive: true).Count;
        OnPropertyChanged(nameof(IsStarving));
        OnPropertyChanged(nameof(NextPostText));
        OnPropertyChanged(nameof(StretchSummary));
    }

    public GroupConfig ToConfig() => new()
    {
        Name = _name,
        ChatId = _chatId,
        Folder = _folder,
        Schedule = _useInterval
            ? new ScheduleConfig { IntervalMinutes = _intervalMinutes }
            : new ScheduleConfig { Hour = _hour, Minute = _minute },
        // Only write it when disabled. Absent already means enabled.
        Enabled = _isEnabled ? null : false,
        JitterMinutes = _jitterMinutes,
        FilesPerPost = _filesPerPost,
        PostOrder = _postOrder,
        // Leave it out while it matches the default, to keep the file tidy.
        ComicOrder = _comicOrder == "name" && _saved.ComicOrder is null ? null : _comicOrder,
        Stretch = _stretchEnabled
            ? new StretchConfig { TargetDays = _stretchTargetDays, MaxIntervalMinutes = _stretchCapMinutes }
            : null,

        // Carried through rather than rebuilt, because nothing here edits them and saving
        // writes the whole file. Dropping start_offset_minutes would put every group back on
        // the same minute of the hour, and Extra is everything the bot has that we do not.
        StartOffsetMinutes = _saved.StartOffsetMinutes,
        Extra = _saved.Extra,
    };

    public void AcceptChanges()
    {
        _saved = ToConfig();
        IsDirty = false;
    }

    public void Revert()
    {
        var config = _saved;
        _name = config.Name;
        _chatId = config.ChatId;
        _folder = config.Folder;
        _useInterval = config.Schedule.IntervalMinutes is > 0;
        _intervalMinutes = config.Schedule.IntervalMinutes ?? 60;
        _hour = config.Schedule.Hour ?? 12;
        _minute = config.Schedule.Minute ?? 0;
        _jitterMinutes = config.JitterMinutes ?? 15;
        _filesPerPost = config.FilesPerPost ?? 1;
        _postOrder = Normalize(config.PostOrder, PostOrders, "oldest");
        _comicOrder = Normalize(config.ComicOrder, ComicOrders, "name");
        _isEnabled = config.Enabled ?? true;
        _stretchEnabled = config.Stretch?.TargetDays is > 0;
        _stretchTargetDays = config.Stretch?.TargetDays ?? 7;
        _stretchCapMinutes = config.Stretch?.MaxIntervalMinutes ?? QueueRunway.DefaultCapMinutes;

        foreach (var name in new[]
                 {
                     nameof(Name), nameof(ChatId), nameof(Folder), nameof(UseInterval),
                     nameof(IntervalMinutes), nameof(Hour), nameof(Minute), nameof(JitterMinutes),
                     nameof(FilesPerPost), nameof(PostOrder), nameof(ComicOrder),
                     nameof(IsEnabled), nameof(RowOpacity),
                     nameof(StretchEnabled), nameof(StretchTargetDays), nameof(StretchCapMinutes),
                     nameof(StretchSummary),
                     nameof(ScheduleSummary), nameof(NextPostText), nameof(ResolvedFolder),
                 })
        {
            OnPropertyChanged(name);
        }

        IsDirty = false;
        RefreshCounts();
    }

    private bool SetEdited<T>(ref T field, T value, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        if (!SetField(ref field, value, name))
            return false;
        IsDirty = true;
        return true;
    }

    private static string Normalize(string? value, string[] allowed, string fallback) =>
        value is not null && allowed.Contains(value, StringComparer.OrdinalIgnoreCase)
            ? value.ToLowerInvariant()
            : fallback;
}
