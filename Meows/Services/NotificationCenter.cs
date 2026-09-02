using System.Collections.ObjectModel;
using Avalonia.Threading;
using Meows.Plugins.Abstractions;

namespace Meows.Services;

/// <summary>One row in the notification list, raised by a plugin or by the shell.</summary>
public sealed class NotificationItem
{
    public required string Source { get; init; }

    public required NotificationSeverity Severity { get; init; }

    public required string Title { get; init; }

    public string Message { get; init; } = "";

    public NotificationAction? Action { get; init; }

    /// <summary>Set for conditions, null for one-off events.</summary>
    public string? ConditionKey { get; init; }

    public DateTime Raised { get; } = DateTime.Now;

    public bool IsCondition => ConditionKey is not null;

    /// <summary>Conditions are not user-dismissable. Only the plugin knows if it still applies.</summary>
    public bool CanDismiss => !IsCondition;

    public bool HasMessage => Message.Length > 0;

    public bool HasAction => Action is not null;

    public string ActionLabel => Action?.Label ?? "";

    public string TimeText => Raised.ToString("HH:mm");

    public string Glyph => Severity switch
    {
        NotificationSeverity.Error => "⛔",
        NotificationSeverity.Warning => "⚠",
        _ => "ℹ",
    };

    public string Accent => Severity switch
    {
        NotificationSeverity.Error => "#FF8A8A",
        NotificationSeverity.Warning => "#E0B25E",
        _ => "#7FB4E0",
    };
}

/// <summary>
/// One notification surface for the whole app. Condition keys are scoped per plugin, so a
/// repeated check replaces its own entry instead of stacking up copies.
/// </summary>
public sealed class NotificationCenter
{
    private const int MaxEvents = 200;

    public ObservableCollection<NotificationItem> Items { get; } = new();

    public event Action? Changed;

    public int Count => Items.Count;

    public bool HasAny => Items.Count > 0;

    public NotificationSeverity? Worst => Items.Count == 0
        ? null
        : Items.Max(i => i.Severity);

    public void Post(string source, NotificationSeverity severity, string title, string message,
        NotificationAction? action)
    {
        OnUiThread(() =>
        {
            Items.Insert(0, new NotificationItem
            {
                Source = source,
                Severity = severity,
                Title = title,
                Message = message,
                Action = action,
            });

            // Only trim events. Conditions stay until their plugin clears them.
            while (Items.Count(i => !i.IsCondition) > MaxEvents)
            {
                var oldest = Items.Last(i => !i.IsCondition);
                Items.Remove(oldest);
            }

            Changed?.Invoke();
        });
    }

    public void SetCondition(string source, string key, NotificationSeverity severity, string title,
        string message, NotificationAction? action)
    {
        OnUiThread(() =>
        {
            RemoveCondition(source, key);
            Items.Insert(0, new NotificationItem
            {
                Source = source,
                Severity = severity,
                Title = title,
                Message = message,
                Action = action,
                ConditionKey = key,
            });
            Changed?.Invoke();
        });
    }

    public void ClearCondition(string source, string key) =>
        OnUiThread(() =>
        {
            if (RemoveCondition(source, key))
                Changed?.Invoke();
        });

    public void Dismiss(NotificationItem item) =>
        OnUiThread(() =>
        {
            if (item.CanDismiss && Items.Remove(item))
                Changed?.Invoke();
        });

    public void DismissAllEvents() =>
        OnUiThread(() =>
        {
            foreach (var item in Items.Where(i => i.CanDismiss).ToList())
                Items.Remove(item);
            Changed?.Invoke();
        });

    /// <summary>Called on deactivation, to take down everything that plugin raised.</summary>
    public void RemoveAllFrom(string source) =>
        OnUiThread(() =>
        {
            foreach (var item in Items.Where(i => i.Source == source).ToList())
                Items.Remove(item);
            Changed?.Invoke();
        });

    private bool RemoveCondition(string source, string key)
    {
        var existing = Items.FirstOrDefault(i => i.Source == source && i.ConditionKey == key);
        return existing is not null && Items.Remove(existing);
    }

    private static void OnUiThread(Action action)
    {
        if (Dispatcher.UIThread.CheckAccess())
            action();
        else
            Dispatcher.UIThread.Post(action);
    }
}

/// <summary>What a plugin actually gets, with its own name baked in as the source.</summary>
public sealed class PluginNotifications : IMeowsNotifications
{
    private readonly NotificationCenter _center;
    private readonly string _source;

    public PluginNotifications(NotificationCenter center, string source)
    {
        _center = center;
        _source = source;
    }

    public void Post(NotificationSeverity severity, string title, string message = "",
        NotificationAction? action = null) =>
        _center.Post(_source, severity, title, message, action);

    public void SetCondition(string key, NotificationSeverity severity, string title, string message = "",
        NotificationAction? action = null) =>
        _center.SetCondition(_source, key, severity, title, message, action);

    public void ClearCondition(string key) => _center.ClearCondition(_source, key);
}
