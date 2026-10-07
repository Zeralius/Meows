using System.Xml.Linq;
using Avalonia.Headless.XUnit;
using Meows.Plugins.Abstractions;
using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// What reaches Windows while the window is not in front, and what a pressed toast does.
///
/// None of this shows a toast: <see cref="ToastRule"/>, <see cref="ToastContent"/> and the relay's
/// pressing are all decided before Windows is asked anything, which is what lets them be tested
/// on a machine, or a build server, where notifications are off.
/// </summary>
public sealed class ToastTests
{
    private static NotificationItem Event(NotificationSeverity severity, string title = "t", params NotificationAction[] actions) =>
        new() { Source = "Portion", Severity = severity, Title = title, Actions = actions };

    private static NotificationItem Condition(string title, string message = "", NotificationSeverity severity = NotificationSeverity.Info) =>
        new() { Source = "Collar", Severity = severity, Title = title, Message = message, ConditionKey = "due" };

    // ---- The rule ------------------------------------------------------------------------

    [Fact]
    public void Nothing_is_toasted_when_they_are_off_or_the_window_is_being_looked_at()
    {
        var urgent = Event(NotificationSeverity.Error);

        Assert.False(ToastRule.ShouldToast(urgent, null, ToastModes.Off, windowInFront: false));
        Assert.False(ToastRule.ShouldToast(urgent, null, ToastModes.Everything, windowInFront: true));
    }

    [Fact]
    public void A_date_come_round_is_toasted_although_it_is_only_info()
    {
        // Collar raises "due soon" at the Info level. A rule by severity would miss the one
        // notification the whole feature is for, which is why the rule is by kind.
        Assert.True(ToastRule.ShouldToast(Condition("2 are due"), null, ToastModes.Wanted, windowInFront: false));
    }

    [Fact]
    public void A_condition_re_set_with_the_same_words_is_not_news()
    {
        // Collar looks at its dates on every pass and sets the same condition each time.
        var yesterday = Condition("2 are due", "TÜV, domain");
        var today = Condition("2 are due", "TÜV, domain");

        Assert.False(ToastRule.ShouldToast(today, yesterday, ToastModes.Wanted, windowInFront: false));
    }

    [Theory]
    [InlineData("3 are due", "TÜV, domain", NotificationSeverity.Info)]
    [InlineData("2 are due", "TÜV, insurance", NotificationSeverity.Info)]
    [InlineData("2 are due", "TÜV, domain", NotificationSeverity.Warning)]
    public void A_condition_whose_words_or_weight_changed_is_toasted_again(string title, string message, NotificationSeverity severity)
    {
        var before = Condition("2 are due", "TÜV, domain");
        var after = Condition(title, message, severity);

        Assert.True(ToastRule.ShouldToast(after, before, ToastModes.Wanted, windowInFront: false));
    }

    [Fact]
    public void An_event_that_merely_finished_waits_unless_everything_was_asked_for()
    {
        var finished = Event(NotificationSeverity.Info, "Scan finished");

        Assert.False(ToastRule.ShouldToast(finished, null, ToastModes.Wanted, windowInFront: false));
        Assert.True(ToastRule.ShouldToast(finished, null, ToastModes.Everything, windowInFront: false));
        Assert.True(ToastRule.ShouldToast(Event(NotificationSeverity.Warning), null, ToastModes.Wanted, windowInFront: false));
    }

    [Fact]
    public void An_unknown_mode_reads_as_the_default_rather_than_as_off()
    {
        Assert.Equal(ToastModes.Wanted, ToastModes.Tidy("loud"));
        Assert.Equal(ToastModes.Wanted, ToastModes.Tidy(null));
        Assert.True(ToastRule.ShouldToast(Condition("due"), null, "loud", windowInFront: false));
    }

    // ---- The toast itself ---------------------------------------------------------------

    [Fact]
    public void A_title_with_markup_in_it_is_text_rather_than_a_toast_windows_refuses()
    {
        var item = Event(NotificationSeverity.Warning, "Fish & chips <\"today\">");

        var xml = XElement.Parse(ToastContent.Xml(item, "Portion"));

        Assert.Equal("Fish & chips <\"today\">", xml.Descendants("text").First().Value);
    }

    [Fact]
    public void Only_five_buttons_reach_windows()
    {
        var many = Enumerable.Range(1, 8).Select(i => new NotificationAction($"b{i}", () => { })).ToArray();

        var xml = XElement.Parse(ToastContent.Xml(Event(NotificationSeverity.Warning, "t", many), "Portion"));

        Assert.Equal(ToastContent.MaxButtons, xml.Descendants("action").Count());
    }

    [Fact]
    public void A_warning_with_buttons_stays_up_and_one_without_does_not()
    {
        var button = new NotificationAction("Shrink", () => { });

        var stays = XElement.Parse(ToastContent.Xml(Event(NotificationSeverity.Warning, "t", button), "Portion"));
        var slides = XElement.Parse(ToastContent.Xml(Event(NotificationSeverity.Warning), "Portion"));
        var info = XElement.Parse(ToastContent.Xml(Event(NotificationSeverity.Info, "t", button), "Portion"));

        Assert.Equal("reminder", stays.Attribute("scenario")?.Value);
        Assert.Null(slides.Attribute("scenario"));
        Assert.Null(info.Attribute("scenario"));
    }

    [Fact]
    public void A_condition_keeps_its_toast_across_every_re_set_and_an_event_has_its_own()
    {
        // The item behind a condition is replaced each time its plugin sets it, so a toast named
        // for the item would be orphaned by the next pass.
        Assert.Equal(ToastContent.Tag(Condition("2 are due")), ToastContent.Tag(Condition("3 are due")));
        Assert.NotEqual(ToastContent.Tag(Event(NotificationSeverity.Error)), ToastContent.Tag(Event(NotificationSeverity.Error)));
        Assert.True(ToastContent.Tag(Condition("x")).Length <= 64, "Windows refuses a tag longer than 64.");
    }

    [Fact]
    public void A_button_says_which_notification_and_which_button_and_says_it_back()
    {
        var condition = new NotificationItem
        {
            Source = "Some|Plugin", Severity = NotificationSeverity.Info, Title = "t", ConditionKey = "key|with|bars",
        };

        var parsed = ToastContent.Parse(ToastContent.Arguments(condition, 2));

        Assert.NotNull(parsed);
        Assert.Equal("Some|Plugin", parsed.Source);
        Assert.Equal("key|with|bars", parsed.ConditionKey);
        Assert.Equal(2, parsed.Button);

        var anEvent = Event(NotificationSeverity.Error);
        var body = ToastContent.Parse(ToastContent.Arguments(anEvent, -1));
        Assert.Equal(anEvent.Id, body?.EventId);
        Assert.True(body?.IsBody);

        Assert.Null(ToastContent.Parse("something else entirely"));
        Assert.Null(ToastContent.Parse(null));
    }

    // ---- The centre saying what changed -------------------------------------------------

    [AvaloniaFact]
    public void A_re_set_condition_arrives_naming_what_it_replaced_and_is_not_reported_as_removed()
    {
        var centre = new NotificationCenter();
        var arrived = new List<(NotificationItem Item, NotificationItem? Replaced)>();
        var removed = new List<NotificationItem>();
        centre.Arrived += (item, replaced) => arrived.Add((item, replaced));
        centre.Removed += removed.Add;

        centre.SetCondition("Collar", "due", NotificationSeverity.Info, "2 are due", "", action: null);
        centre.SetCondition("Collar", "due", NotificationSeverity.Info, "2 are due", "", action: null);

        Assert.Equal(2, arrived.Count);
        Assert.Null(arrived[0].Replaced);
        Assert.Same(arrived[0].Item, arrived[1].Replaced);
        Assert.Empty(removed);
        Assert.Single(centre.Items);

        centre.ClearCondition("Collar", "due");
        Assert.Single(removed);
    }

    // ---- Pressing -----------------------------------------------------------------------

    private static (ToastRelay Relay, NotificationCenter Centre, List<string> Opened) Relay()
    {
        var centre = new NotificationCenter();
        var opened = new List<string>();
        var toasts = new WindowsToasts(Path.GetTempPath(), _ => { });
        // Off, so arriving notifications never ask Windows for anything during a test.
        var relay = new ToastRelay(centre, toasts, () => ToastModes.Off, () => false, () => opened.Add("window"), _ => { });
        return (relay, centre, opened);
    }

    [AvaloniaFact]
    public void A_button_on_yesterdays_toast_reaches_todays_condition()
    {
        var (relay, centre, opened) = Relay();
        using var _ = relay;
        var pressed = new List<string>();

        centre.SetCondition("Collar", "due", NotificationSeverity.Info, "2 are due", "",
            [new NotificationAction("Done", () => pressed.Add("yesterday"))]);
        var yesterday = centre.Items.Single();
        var target = ToastContent.Parse(ToastContent.Arguments(yesterday, 0));

        // The plugin looked again and set it afresh: a new item with new actions.
        centre.SetCondition("Collar", "due", NotificationSeverity.Info, "2 are due", "",
            [new NotificationAction("Done", () => pressed.Add("today"))]);

        relay.Press(target);

        Assert.Equal(["today"], pressed);
        Assert.Empty(opened);
    }

    [AvaloniaFact]
    public void Pressing_the_toast_itself_or_a_button_that_is_gone_opens_the_window()
    {
        var (relay, centre, opened) = Relay();
        using var _ = relay;

        centre.Post("Portion", NotificationSeverity.Warning, "Would fail", "", action: null);
        var item = centre.Items.Single();

        relay.Press(ToastContent.Parse(ToastContent.Arguments(item, -1)));
        relay.Press(ToastContent.Parse(ToastContent.Arguments(item, 3)));
        relay.Press(null);

        Assert.Equal(3, opened.Count);
    }

    [AvaloniaFact]
    public void A_button_that_says_it_dismisses_takes_its_event_down()
    {
        var (relay, centre, _) = Relay();
        using var _r = relay;
        var shrunk = false;

        centre.Post("Portion", NotificationSeverity.Warning, "Would fail", "",
            [new NotificationAction("Shrink", () => shrunk = true, DismissesAfter: true)]);
        var item = centre.Items.Single();

        relay.Press(ToastContent.Parse(ToastContent.Arguments(item, 0)));

        Assert.True(shrunk);
        Assert.Empty(centre.Items);
    }
}
