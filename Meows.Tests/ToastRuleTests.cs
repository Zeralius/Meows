using Avalonia.Headless.XUnit;
using Meows.Plugins.Abstractions;
using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// What is worth saying outside the window: every event, and a condition when it is new or its
/// words change, never on every pass that re-sets it the same.
/// </summary>
public sealed class ToastRuleTests
{
    /// <summary>
    /// The centre hops to the UI thread when it is not on it, and the test thread only counts
    /// as the UI thread when the headless app has been touched first. Touch it.
    /// </summary>
    [AvaloniaFact]
    public void A_condition_is_said_when_new_or_changed_and_not_when_re_set_the_same()
    {
        var centre = new NotificationCenter();
        var said = new List<string>();
        centre.ConditionSet += (item, replaced) =>
        {
            if (ToastRule.ShouldSay(item, replaced))
                said.Add(item.Title);
        };

        centre.SetCondition("meows.collar", "due", NotificationSeverity.Info, "2 are due", "", []);
        centre.SetCondition("meows.collar", "due", NotificationSeverity.Info, "2 are due", "", []);
        centre.SetCondition("meows.collar", "due", NotificationSeverity.Info, "3 are due", "", []);
        centre.SetCondition("meows.collar", "due", NotificationSeverity.Warning, "3 are due", "", []);
        centre.SetCondition("meows.vet", "due", NotificationSeverity.Info, "3 are due", "", []);

        Assert.Equal(["2 are due", "3 are due", "3 are due", "3 are due"], said);
        Assert.Equal(2, centre.Count);
    }

    [AvaloniaFact]
    public void A_condition_cleared_and_raised_again_is_new()
    {
        var centre = new NotificationCenter();
        NotificationItem? lastReplaced = null;
        centre.ConditionSet += (_, replaced) => lastReplaced = replaced;

        centre.SetCondition("meows.vet", "health", NotificationSeverity.Warning, "Vet", "S: is low", []);
        centre.SetCondition("meows.vet", "health", NotificationSeverity.Warning, "Vet", "S: is low", []);
        Assert.NotNull(lastReplaced);

        centre.ClearCondition("meows.vet", "health");
        centre.SetCondition("meows.vet", "health", NotificationSeverity.Warning, "Vet", "S: is low", []);
        Assert.Null(lastReplaced);
    }

    [Fact]
    public void An_event_is_said_unless_only_trouble_is_wanted_and_it_is_only_news()
    {
        var finished = new NotificationItem { Source = "meows.chonk", Title = "Measured", Severity = NotificationSeverity.Info };
        var failed = new NotificationItem { Source = "meows.chonk", Title = "Could not read D:", Severity = NotificationSeverity.Warning };

        Assert.True(ToastRule.ShouldSay(finished, null));
        Assert.True(ToastRule.ShouldSay(failed, null));

        Assert.False(ToastRule.ShouldSay(finished, null, onlyTrouble: true));
        Assert.True(ToastRule.ShouldSay(failed, null, onlyTrouble: true));
    }

    [Fact]
    public void A_condition_wants_doing_whatever_its_severity()
    {
        var due = new NotificationItem
        {
            Source = "meows.collar", Title = "2 are due", Severity = NotificationSeverity.Info, ConditionKey = "due",
        };
        Assert.True(ToastRule.ShouldSay(due, null, onlyTrouble: true));
    }
}
