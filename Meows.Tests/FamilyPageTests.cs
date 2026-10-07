using Meows.Plugins.Abstractions;
using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// The week on one page: what is up now, what the week held, and what happened since the
/// window was last hidden. Markdown, so anything prints it.
/// </summary>
public sealed class FamilyPageTests
{
    private static IMeowsText Text() => TestStrings.Load();

    [Fact]
    public void The_page_names_what_is_up_what_the_week_held_and_what_happened()
    {
        var text = TestStrings.Load();
        var page = FamilyPage.Of(
            [
                new FamilyPage.CardLines("Collar", [new Glance("1 has passed", IsTrouble: true), new Glance("")]),
                new FamilyPage.CardLines("Quiet", []),
            ],
            ["3 things done"],
            [("Basket", "Milk"), ("Collar", "TÜV")],
            new DateTime(2026, 10, 5),
            text);

        Assert.Contains("# Family week", page);
        Assert.Contains("- **Collar**: 1 has passed", page);
        Assert.DoesNotContain("Quiet", page);
        Assert.Contains("- 3 things done", page);
        Assert.Contains("- **Basket**: Milk", page);
        Assert.Contains("## Up now", page);
        Assert.Contains("## This week", page);
        Assert.Contains("## Since last hidden", page);
    }

    [Fact]
    public void Empty_sections_read_as_quiet_weeks_and_recent_is_capped()
    {
        var text = TestStrings.Load();
        var recent = Enumerable.Range(1, 20).Select(i => ("P", $"thing {i}")).ToList();

        var page = FamilyPage.Of([], [], recent, new DateTime(2026, 10, 5), text);

        Assert.Equal(2, page.Split("Quiet.").Length - 1);
        Assert.Contains("thing 1", page);
        Assert.DoesNotContain("thing 20", page);
    }

    [Fact]
    public void German_reads_german()
    {
        var text = TestStrings.Load();
        text.Use("de");

        var page = FamilyPage.Of([], [], [], new DateTime(2026, 10, 5), text);

        Assert.Contains("# Familienwoche", page);
        Assert.Contains("## Steht an", page);
    }
}
