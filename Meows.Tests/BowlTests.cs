using Meows.Plugins.Abstractions;
using Meows.Plugins.WeighIn;
using Meows.Plugins.WeighIn.Services;
using Meows.Plugins.WeighIn.ViewModels;
using Meows.Services;

namespace Meows.Tests;

/// <summary>
/// Folder budgets in Weigh-In: a line set by hand, the folder measured at every reading at
/// whatever depth it sits, a warning up while it is over and gone the day it is not, and one
/// line in the history the reading it crosses.
/// </summary>
public sealed class BowlTests : IDisposable
{
    private const long GB = BudgetRowViewModel.Gigabyte;

    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-bowl-" + Guid.NewGuid().ToString("N")[..8]);

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

    private string F => _root;

    private string Under(params string[] parts) => Path.Combine([_root, .. parts]);

    private Reading At(DateTime at, params (string Path, long Size)[] budgeted) =>
        new(at, [new DriveReading(F, 1000 * GB, 100 * GB, [])], budgeted.Select(b => new FolderReading(b.Path, b.Size)).ToList());

    [Fact]
    public void A_reading_keeps_a_budgeted_folder_at_any_depth_apart_from_the_drive_folders()
    {
        var deep = Path.Combine(_root, "Users", "Dennis", "Downloads");
        Directory.CreateDirectory(deep);
        File.WriteAllBytes(Path.Combine(deep, "big.bin"), new byte[3000]);
        File.WriteAllBytes(Path.Combine(_root, "Users", "other.bin"), new byte[500]);

        var reading = Readings.Take([_root], depth: 1, skipSystemFolders: false, null, CancellationToken.None,
            budgeted: [deep, Path.Combine(_root, "Nowhere")]);

        Assert.Equal(3000, reading.SizeOf(deep));
        Assert.Equal(3000, reading.SizeOf(deep + Path.DirectorySeparatorChar));
        Assert.Null(reading.SizeOf(Path.Combine(_root, "Nowhere")));
        Assert.DoesNotContain(reading.Drives.Single().Folders, f => Budgets.Same(f.Path, deep));
    }

    [Fact]
    public void Readings_from_before_budgets_still_load_and_measure_nothing()
    {
        var folder = Path.Combine(_root, "readings");
        Readings.Save(folder, new Reading(DateTime.Today.AddDays(-1), [new DriveReading(F, 10, 5, [])]));
        Readings.Save(folder, At(DateTime.Today, (Under("Downloads"), 7 * GB)));

        var loaded = Readings.Load(folder);

        Assert.Null(loaded[0].SizeOf(Under("Downloads")));
        Assert.Equal(7 * GB, loaded[1].SizeOf(Under("downloads") + Path.DirectorySeparatorChar));
    }

    [Fact]
    public void Over_is_over_only_by_the_reading_and_crossing_is_said_once()
    {
        var downloads = new FolderBudget { Path = Under("Downloads"), Bytes = 20 * GB };
        var recordings = new FolderBudget { Path = Under("Recordings"), Bytes = 200 * GB };
        FolderBudget[] budgets = [downloads, recordings];

        var monday = At(DateTime.Today.AddDays(-2), (Under("Downloads"), 18 * GB));
        var tuesday = At(DateTime.Today.AddDays(-1), (Under("Downloads"), 23 * GB));
        var wednesday = At(DateTime.Today, (Under("Downloads"), 25 * GB));

        var standings = Budgets.Check(budgets, tuesday);
        Assert.Equal(downloads, standings[0].Budget);
        Assert.True(standings[0].IsOver);
        Assert.Equal(3 * GB, standings[0].Over);
        Assert.False(standings[1].Measured);
        Assert.False(standings[1].IsOver);

        Assert.Equal([downloads], Budgets.Crossed(budgets, tuesday, monday).Select(s => s.Budget));
        Assert.Empty(Budgets.Crossed(budgets, wednesday, tuesday));
        Assert.Single(Budgets.Crossed(budgets, tuesday, null));
    }

    private WeighInViewModel Open(FakeHost host, params Reading[] readings)
    {
        var folder = Path.Combine(host.DataDirectory, "readings");
        foreach (var reading in readings)
            Readings.Save(folder, reading);
        return new WeighInViewModel(host);
    }

    [Fact]
    public void The_tab_warns_while_a_folder_is_over_and_clears_the_day_it_is_not()
    {
        var host = new FakeHost(Path.Combine(_root, "host"));
        host.SaveSettings(new WeighInSettings { Budgets = [new FolderBudget { Path = Under("Downloads"), Bytes = 20 * GB }] });

        using var model = Open(host, At(DateTime.Today, (Under("Downloads"), 23 * GB)));

        var row = Assert.Single(model.BudgetRows);
        Assert.True(row.IsOver);
        Assert.Contains("3 GB over", row.StandingText);
        Assert.True(host.Conditions.ContainsKey("budget"));
        var glance = model.Glance();
        Assert.True(glance!.IsTrouble);
        Assert.Contains("Downloads", glance.Text);

        // Raised past where the folder is: the warning goes at once, without waiting for a reading.
        row.Gigabytes = 30;
        Assert.False(host.Conditions.ContainsKey("budget"));
        Assert.False(model.BudgetRows.Single().IsOver);
        Assert.False(model.Glance()!.IsTrouble);
        Assert.Equal(30 * GB, host.LoadSettings<WeighInSettings>()!.Budgets.Single().Bytes);

        model.BudgetRows.Single().Gigabytes = 10;
        Assert.True(host.Conditions.ContainsKey("budget"));
        model.RemoveBudgetCommand.Execute(model.BudgetRows.Single());
        Assert.Empty(model.BudgetRows);
        Assert.False(host.Conditions.ContainsKey("budget"));
    }

    [Fact]
    public void A_new_budget_starts_above_the_folder_and_one_not_yet_measured_waits_for_the_next_reading()
    {
        var host = new FakeHost(Path.Combine(_root, "host-add"));
        using var model = Open(host, new Reading(DateTime.Today, [new DriveReading(F, 1000 * GB, 100 * GB,
            [new FolderReading(Under("Games"), 12 * GB)])]));

        model.AddBudget(Under("Games"));
        Assert.Equal(15 * GB, host.LoadSettings<WeighInSettings>()!.Budgets.Single().Bytes);

        model.AddBudget(Under("Photos", "2026"));
        var fresh = model.BudgetRows.Single(r => r.Path == Under("Photos", "2026"));
        Assert.Contains("next reading", fresh.StandingText);
        Assert.Equal(10 * GB, fresh.Budget.Bytes);

        model.AddBudget(Under("Games") + Path.DirectorySeparatorChar);
        Assert.Equal(2, model.BudgetRows.Count);
    }

    [Fact]
    public void With_no_window_the_home_line_says_a_folder_is_over_before_anything_else()
    {
        var settings = new ShellSettings(Path.Combine(_root, "settings"), Path.Combine(_root, "no-mews"));
        settings.SavePluginSettings("meows.weighin", new WeighInSettings { Budgets = [new FolderBudget { Path = Under("Downloads"), Bytes = 20 * GB }] });
        var folder = Path.Combine(settings.PluginDataDirectory("meows.weighin"), "readings");
        Readings.Save(folder, At(DateTime.Today, (Under("Downloads"), 21 * GB)));

        var glance = new WeighInPlugin().GlanceWhileOff(new DormantHost("meows.weighin", settings, NoHandoff.Instance, null));

        Assert.NotNull(glance);
        Assert.True(glance!.IsTrouble);
        Assert.Equal("Downloads is 1 GB over its budget", glance.Text);
    }
}
