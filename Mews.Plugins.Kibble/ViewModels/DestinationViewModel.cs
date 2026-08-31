using Mews.Bot;
using Mews.Plugins.Abstractions;

namespace Mews.Plugins.Kibble.ViewModels;

/// <summary>
/// One group as a destination. Leads with days of runway rather than the file count, because
/// the count on its own does not tell you which group needs feeding.
/// </summary>
public sealed class DestinationViewModel : ObservableObject
{
    private readonly BotWorkspace _workspace;
    private int _queued;
    private double? _days;

    public DestinationViewModel(GroupConfig group, BotWorkspace workspace, int index)
    {
        Group = group;
        _workspace = workspace;
        Index = index;
        Refresh();
    }

    public GroupConfig Group { get; }

    /// <summary>Position in the list, so 1 to 9 can be typed instead of clicked.</summary>
    public int Index { get; }

    public string Name => string.IsNullOrWhiteSpace(Group.Name) ? "(unnamed)" : Group.Name;

    /// <summary>Only the first nine get a key. Past that you click.</summary>
    public string ShortcutText => Index <= 9 ? Index.ToString() : "";

    public bool HasShortcut => Index <= 9;

    public int Queued
    {
        get => _queued;
        private set
        {
            if (SetField(ref _queued, value))
                OnPropertyChanged(nameof(QueueText));
        }
    }

    public double? Days
    {
        get => _days;
        private set
        {
            if (!SetField(ref _days, value))
                return;
            OnPropertyChanged(nameof(RunwayText));
            OnPropertyChanged(nameof(IsLow));
            OnPropertyChanged(nameof(IsDry));
            OnPropertyChanged(nameof(Accent));
        }
    }

    public string RunwayText => QueueRunway.Describe(Days);

    public string QueueText => $"{Queued} queued";

    public bool IsDisabled => Group.Enabled == false;

    public bool IsDry => Days is not null && Queued == 0;

    public bool IsLow => Days is not null && Days > 0 && Days < QueueRunway.LowDays;

    /// <summary>Colour carries the urgency, so the list reads without being read.</summary>
    public string Accent => IsDry ? "#FF8A8A" : IsLow ? "#E0B25E" : "#5E9E6E";

    public void Refresh()
    {
        Queued = _workspace.Scan(_workspace.ToSendFolder(Group)).Count;
        Days = QueueRunway.Days(Group, Queued);
    }
}
