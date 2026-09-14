using Meows.Plugins.Abstractions;
using Meows.Plugins.Collar.Services;
using Meows.Plugins.Collar.ViewModels;
using Avalonia.Headless.XUnit;
using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// A notification with more than one button, and the buttons doing what they say. Since
/// contract 0.8.0; before that a notification had one button and most had none.
/// </summary>
public sealed class NotificationActionTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "notify-" + Guid.NewGuid().ToString("N")[..10]);

    public NotificationActionTests() => Directory.CreateDirectory(_root);

    /// <summary>
    /// The centre hops to the UI thread when it is not on it, and the test thread only counts
    /// as the UI thread when the headless app has been touched first. Touch it.
    /// </summary>
    [AvaloniaFact]
    public void The_centre_keeps_every_button_and_the_first_is_still_the_action()
    {
        var centre = new NotificationCenter();
        var pressed = new List<string>();

        centre.Post("meows.x", NotificationSeverity.Info, "Two things", "",
            [new NotificationAction("One", () => pressed.Add("one")), new NotificationAction("Two", () => pressed.Add("two"), DismissesAfter: true)]);

        var item = centre.Items.Single();
        Assert.Equal(2, item.Actions.Count);
        Assert.Equal("One", item.Action?.Label);
        Assert.Equal(["One", "Two"], item.Buttons.Select(b => b.Label));

        // An old caller with one button, or none, reads the same as before.
        centre.Post("meows.x", NotificationSeverity.Info, "One thing", "", new NotificationAction("Only", () => { }));
        centre.Post("meows.x", NotificationSeverity.Info, "Nothing", "", action: null);
        Assert.True(centre.Items[1].HasAction);
        Assert.False(centre.Items[0].HasAction);
        Assert.Empty(centre.Items[0].Buttons);
    }

    [Fact]
    public void The_default_on_the_contract_hands_an_older_shell_the_first_button()
    {
        // A shell built before 0.8.0 implements only the one-button shape. The interface's
        // default turns a many-button call into that, first button kept, so the plugin's call
        // still shows something rather than nothing.
        var old = new OneButtonShell();
        IMeowsNotifications notifications = old;

        notifications.Post(NotificationSeverity.Info, "t", "m", new NotificationAction("A", () => { }), new NotificationAction("B", () => { }));
        notifications.SetCondition("k", NotificationSeverity.Warning, "t", "m", new NotificationAction("C", () => { }));

        Assert.Equal("A", old.LastPostAction?.Label);
        Assert.Equal("C", old.LastConditionAction?.Label);
    }

    [Fact]
    public void One_date_due_gets_done_and_not_this_week_on_the_notification()
    {
        var host = new FakeHost(_root);
        using var model = new CollarViewModel(host);
        model.Add(new CollarEntry { Title = "TÜV", Due = DateTime.Today.AddDays(-2) });

        var key = host.Conditions.Keys.Single();
        var buttons = host.Buttons[key];
        Assert.Equal(["Done", "Not this week", "Look again"], buttons.Select(b => b.Label));

        buttons[1].Invoke();

        // Snoozed a week from today, not from when it was due. A week away is inside the lead,
        // so it is still news, but as "coming up" rather than "has passed".
        Assert.Equal(DateTime.Today.AddDays(7), model.Entries.Single().Entry.Due);
        Assert.Contains("coming up", host.Conditions.Values.Single());
        Assert.Contains(host.Store.Events, e => e.Kind == "snoozed");
    }

    [Fact]
    public void Done_on_the_notification_is_the_same_as_done_on_the_tab()
    {
        var host = new FakeHost(_root);
        using var model = new CollarViewModel(host);
        model.Add(new CollarEntry { Title = "Rent", Due = DateTime.Today.AddDays(-1), RepeatMonths = 1 });

        host.Buttons[host.Conditions.Keys.Single()][0].Invoke();

        // Monthly, so the next one is inside the lead: still news, no longer overdue.
        Assert.True(model.Entries.Single().Entry.Due > DateTime.Today);
        Assert.DoesNotContain("passed", host.Conditions.Values.Single());
        Assert.Contains(host.Store.Events, e => e.Kind == "handled");
    }

    [Fact]
    public void Several_dates_due_keep_the_one_button_because_a_list_belongs_on_the_tab()
    {
        var host = new FakeHost(_root);
        using var model = new CollarViewModel(host);
        model.Add(new CollarEntry { Title = "A", Due = DateTime.Today.AddDays(-1) });
        model.Add(new CollarEntry { Title = "B", Due = DateTime.Today.AddDays(-3) });

        Assert.Equal(["Look again"], host.Buttons[host.Conditions.Keys.Single()].Select(b => b.Label));
    }

    private sealed class OneButtonShell : IMeowsNotifications
    {
        public NotificationAction? LastPostAction { get; private set; }

        public NotificationAction? LastConditionAction { get; private set; }

        public void Post(NotificationSeverity severity, string title, string message = "", NotificationAction? action = null) =>
            LastPostAction = action;

        public void SetCondition(string key, NotificationSeverity severity, string title, string message = "", NotificationAction? action = null) =>
            LastConditionAction = action;

        public void ClearCondition(string key)
        {
        }
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }
}
