using System.Text.RegularExpressions;
using Meows.Services;
using Meows.ViewModels;

namespace Meows.Tests;

/// <summary>
/// The parts of the 6.0.0 accessibility pass that can be checked without a screen reader: muted
/// text that holds its contrast, no text faded with opacity, names on what has no words, and the
/// name card that replaced the text boxes in the strip's menus.
/// </summary>
public sealed class AccessibilityTests
{
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(here))!;

    private static Dictionary<string, string> Theme(string palette, string variant)
    {
        var start = palette.IndexOf($"x:Key=\"{variant}\"", StringComparison.Ordinal);
        var end = palette.IndexOf("</ResourceDictionary>", start, StringComparison.Ordinal);
        return Regex.Matches(palette[start..end], "x:Key=\"(Meows\\w+)\" Color=\"(#[0-9A-Fa-f]{6})\"")
            .ToDictionary(m => m.Groups[1].Value, m => m.Groups[2].Value);
    }

    [Theory]
    [InlineData("Dark")]
    [InlineData("Light")]
    public void Muted_text_reads_at_four_and_a_half_to_one_on_every_surface(string variant)
    {
        var colours = Theme(File.ReadAllText(Path.Combine(Repo(), "Meows", "Themes", "Palette.axaml")), variant);
        var muted = colours["MeowsTextMuted"];

        foreach (var surface in new[]
                 {
                     "MeowsSunken", "MeowsPanel", "MeowsBase", "MeowsBar", "MeowsCard", "MeowsInset",
                     "MeowsRaised", "MeowsHeader", "MeowsSelection", "MeowsInfo", "MeowsWarn", "MeowsDanger",
                 })
        {
            var ratio = Appearance.Contrast(muted, colours[surface]);
            Assert.True(ratio >= 4.5, $"{variant}: muted on {surface} is {ratio:0.00}:1");
        }
    }

    [Fact]
    public void No_view_fades_its_text_below_what_can_be_read()
    {
        // Opacity at 0.6 or under on a TextBlock was what put secondary text at 2.4:1 on a light
        // card. Above that, 0.65 and up, it still holds 4.5:1 in both themes.
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Repo(), "*.axaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                                 !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            var text = File.ReadAllText(file);
            foreach (Match tag in Regex.Matches(text, "<(?:TextBlock|SelectableTextBlock)\\b[^>]*>"))
            {
                var opacity = Regex.Match(tag.Value, "\\sOpacity=\"([0-9.]+)\"");
                if (opacity.Success && double.Parse(opacity.Groups[1].Value, System.Globalization.CultureInfo.InvariantCulture) <= 0.6)
                    offenders.Add($"{Path.GetFileName(file)}: {tag.Value[..Math.Min(80, tag.Value.Length)]}");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void A_button_with_only_a_symbol_on_it_has_a_name_to_be_read_aloud()
    {
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Repo(), "*.axaml", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}") &&
                                 !f.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}")))
        {
            foreach (Match tag in Regex.Matches(File.ReadAllText(file), "<(?:Button|ToggleButton|RepeatButton)\\b[^>]*>"))
            {
                var content = Regex.Match(tag.Value, "Content=\"([^\"]*)\"");
                if (content.Success && !content.Groups[1].Value.StartsWith('{') &&
                    !Regex.IsMatch(content.Groups[1].Value, "[A-Za-z0-9]") &&
                    !tag.Value.Contains("AutomationProperties.Name", StringComparison.Ordinal))
                    offenders.Add($"{Path.GetFileName(file)}: {content.Groups[1].Value}");
            }
        }

        Assert.Empty(offenders);
    }

    [Fact]
    public void The_name_card_hands_over_what_was_typed_and_nothing_on_cancel()
    {
        var prompt = new PromptViewModel();
        string? got = null;

        prompt.Ask("Rename", "Disk", text => got = text);
        Assert.True(prompt.IsOpen);
        prompt.Text = "  Drives  ";
        prompt.OkCommand.Execute(null);

        Assert.Equal("Drives", got);
        Assert.False(prompt.IsOpen);

        got = null;
        prompt.Ask("Rename", "Disk", text => got = text);
        prompt.CancelCommand.Execute(null);
        Assert.Null(got);
        Assert.False(prompt.IsOpen);

        // An empty name is not a name.
        prompt.Ask("Rename", "", text => got = text);
        Assert.False(prompt.OkCommand.CanExecute(null));
    }

    [Fact]
    public void A_tab_says_its_group_and_whether_it_is_in_front()
    {
        TestStrings.Install();
        var tab = new TabViewModel("shell.tab.home", "🏠", new object());
        var plain = tab.AccessibleName;

        tab.GroupName = "Disk";
        Assert.Contains("Disk", tab.AccessibleName);
        Assert.StartsWith(plain, tab.AccessibleName);

        var unselected = tab.AccessibleName;
        tab.IsSelected = true;
        Assert.NotEqual(unselected, tab.AccessibleName);
    }

    [Fact]
    public void A_two_step_button_wants_a_second_press_that_is_not_the_same_double_click()
    {
        var now = new DateTime(2026, 10, 12, 12, 0, 0, DateTimeKind.Utc);
        var step = new TwoStep(() => "Delete", () => "Sure?", () => now);

        Assert.False(step.Press());
        Assert.Equal("Sure?", step.Text);

        now = now.AddMilliseconds(200);
        Assert.False(step.Press());

        now = now.AddSeconds(2);
        Assert.True(step.Press());
        Assert.Equal("Delete", step.Text);

        // Armed and forgotten: a press much later arms it again rather than doing it.
        Assert.False(step.Press());
        now = now.AddMinutes(1);
        Assert.False(step.Press());
        now = now.AddSeconds(1);
        Assert.True(step.Press());
    }

    [Fact]
    public void Text_size_follows_a_pick_and_stays_inside_what_the_window_can_hold()
    {
        Assert.Equal(150, TextScale.Resolve(150));
        Assert.Equal(TextScale.Largest, TextScale.Resolve(400));
        Assert.Equal(TextScale.Smallest, TextScale.Resolve(50));
        Assert.InRange(TextScale.Resolve(null), TextScale.Smallest, TextScale.Largest);
    }
}
