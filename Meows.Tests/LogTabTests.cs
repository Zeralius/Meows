using Avalonia.Headless.XUnit;
using Meows.Plugins.Abstractions;
using Meows.Services;
using Meows.ViewModels;

namespace Meows.Tests;

/// <summary>
/// The log as a tab: filtered by word and source, trouble surfaced, and per source a say in how
/// much shows. The file keeps everything; only what is shown is decided here.
/// </summary>
public sealed class LogTabTests
{
    private static (ShellLog Log, LogViewModel Tab, Dictionary<string, LogLevel> Saved) Open(params (string Source, LogLevel Level)[] levels)
    {
        var log = new ShellLog();
        var saved = new Dictionary<string, LogLevel>(StringComparer.OrdinalIgnoreCase);
        var tab = new LogViewModel(log, levels.ToDictionary(l => l.Source, l => l.Level), s =>
        {
            saved.Clear();
            foreach (var (k, v) in s)
                saved[k] = v;
        });
        return (log, tab, saved);
    }

    private static void Settle() => Avalonia.Threading.Dispatcher.UIThread.RunJobs();

    [AvaloniaFact]
    public void Lines_carry_a_level_and_trouble_is_counted()
    {
        var (log, tab, _) = Open();

        log.Write("Kibble", "queued a.png");
        log.Write("Kibble", "Not sent: b.png", LogLevel.Warning);
        log.Write("Birdwatch", "refresh failed", LogLevel.Error);
        Settle();

        Assert.Equal(3, tab.Visible.Count);
        Assert.Equal(1, tab.Warnings);
        Assert.Equal(1, tab.Errors);
        Assert.True(tab.HasTrouble);
        Assert.Contains("1 warnings, 1 errors", tab.SummaryText);
        Assert.True(tab.Visible[1].IsWarning);
        Assert.Contains("[Kibble] Not sent: b.png", tab.Visible[1].Text);
    }

    [AvaloniaFact]
    public void Only_trouble_and_a_word_and_a_source_each_narrow_the_list()
    {
        var (log, tab, _) = Open();
        log.Write("Kibble", "queued a.png");
        log.Write("Kibble", "Not sent: b.png", LogLevel.Warning);
        log.Write("Birdwatch", "refresh failed", LogLevel.Error);
        Settle();

        tab.OnlyTrouble = true;
        Assert.Equal(2, tab.Visible.Count);

        tab.OnlyTrouble = false;
        tab.Filter = "png";
        Assert.Equal(2, tab.Visible.Count);

        tab.Filter = "";
        tab.SelectedSource = "Birdwatch";
        Assert.Single(tab.Visible);

        tab.ClearFilterCommand.Execute(null);
        Assert.Equal(3, tab.Visible.Count);
    }

    [AvaloniaFact]
    public void A_source_turned_down_shows_only_its_trouble_and_a_quiet_one_nothing_but_the_file_keeps_it()
    {
        var (log, tab, saved) = Open();
        log.Write("Saucer", "clipboard polled");
        log.Write("Saucer", "clipboard polled again");
        log.Write("Saucer", "clipboard locked", LogLevel.Warning);
        Settle();
        Assert.Equal(3, tab.Visible.Count);

        var saucer = tab.Sources.Single(s => s.Source == "Saucer");
        saucer.Selected = LogViewModel.Choices.Single(c => c.Minimum == LogLevel.Warning);
        Assert.Single(tab.Visible);
        Assert.Equal(LogLevel.Warning, saved["Saucer"]);

        saucer.Selected = LogViewModel.Choices.Single(c => c.Minimum == LogViewModel.Quiet);
        Assert.Empty(tab.Visible);
        Assert.True(tab.IsEmpty);

        // Still said, still in the log itself, just not shown. Turning it back up brings it back.
        log.Write("Saucer", "still polling");
        Settle();
        Assert.Equal(4, log.Entries.Count);
        Assert.Empty(tab.Visible);

        saucer.Selected = LogViewModel.Choices.Single(c => c.Minimum == LogLevel.Info);
        Assert.Equal(4, tab.Visible.Count);
        Assert.Empty(saved);
    }

    [AvaloniaFact]
    public void Levels_read_in_at_the_start_apply_from_the_first_line()
    {
        var (log, tab, _) = Open(("Saucer", LogViewModel.Quiet));
        log.Write("Saucer", "polled");
        log.Write("Kibble", "queued");
        Settle();

        Assert.Single(tab.Visible);
        Assert.Equal("Kibble", tab.Visible[0].Source);
        Assert.Equal(2, tab.Sources.Count);
    }
}
