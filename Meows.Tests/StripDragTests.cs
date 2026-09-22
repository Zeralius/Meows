using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Meows.Plugins;
using Meows.Plugins.Abstractions;
using Meows.Services;
using Meows.ViewModels;
using Meows.Views;

namespace Meows.Tests;

/// <summary>
/// The strip driven by an actual pointer rather than by calling the view model.
///
/// <see cref="TabGroupingTests"/> covers where things land, which is arithmetic on strings. This
/// covers the half that arithmetic cannot: whether a press on a tab is seen at all, whether the
/// hit testing finds the right item through the nesting in the item template, whether the
/// threshold tells a drag from a click, and whether a drop is swallowed so it does not also
/// select what it landed on. Every one of those is a thing that works perfectly in the model and
/// does nothing in the window.
/// </summary>
public sealed class StripDragTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-drag-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        PluginNames.Feline = true;
        TestStrings.Install();
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    private sealed class NoInternet : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private MainWindowViewModel Build()
    {
        Directory.CreateDirectory(_root);
        var log = new ShellLog(Path.Combine(_root, "meows.log"));
        var settings = new ShellSettings(_root, Path.Combine(_root, "no-mews"));
        var text = TestStrings.Load();
        MeowsText.Use(text);
        var notifications = new NotificationCenter();
        var background = new BackgroundTaskService(notifications, log);
        var store = new MeowsStore(_root, _ => { });
        var updater = new PluginUpdates("0.0.0-test", new HttpClient(new NoInternet()));
        var model = new MainWindowViewModel(new PluginCatalog(log), settings, log, notifications, background, text,
            settings.LoadPreferences(), store, updater);
        model.Initialize();
        return model;
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }

    /// <summary>The button the strip drew for one tab, which is what a pointer would land on.</summary>
    private static Button Button(Window window, TabViewModel tab) =>
        window.GetVisualDescendants()
            .OfType<Button>()
            .First(b => ReferenceEquals(b.DataContext, tab) && b.Classes.Contains("tab"));

    private static Point Centre(Window window, Control control) =>
        control.TranslatePoint(new Point(control.Bounds.Width / 2, control.Bounds.Height / 2), window)
        ?? throw new InvalidOperationException("The strip item is not in the window's visual tree.");

    /// <summary>The tabs as the strip is showing them, in strip order.</summary>
    private static string[] Order(MainWindowViewModel model) =>
        model.Strip.Items.OfType<TabViewModel>().Select(t => t.Key).ToArray();

    /// <summary>A press, some movement in steps, and a release, the way a hand would do it.</summary>
    private static void Drag(Window window, Point from, Point to)
    {
        window.MouseDown(from, MouseButton.Left);
        Settle();

        // In steps, because one jump would pass the threshold and the target in the same event
        // and prove nothing about the move handling.
        for (var step = 1; step <= 4; step++)
        {
            var at = new Point(from.X + (to.X - from.X) * step / 4.0, from.Y + (to.Y - from.Y) * step / 4.0);
            window.MouseMove(at);
            Settle();
        }

        window.MouseUp(to, MouseButton.Left);
        Settle();
        Settle();
    }

    [AvaloniaFact]
    public void A_tab_dragged_past_its_neighbour_changes_places_with_it()
    {
        var model = Build();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = model };
            model.Window = window;
            window.Show();
            Settle();

            var before = Order(model);
            Assert.True(before.Length >= 3, $"Expected the shell's own tabs on the strip, found {before.Length}.");

            var first = model.Strip.Items.OfType<TabViewModel>().First(t => t.Key == "shell.tab.plugins");
            var third = model.Strip.Items.OfType<TabViewModel>().First(t => t.Key == "shell.tab.history");

            // Onto the far side of the third tab, which is what "drop it after that one" means.
            var landing = Centre(window, Button(window, third));
            Drag(window, Centre(window, Button(window, first)), new Point(landing.X + 12, landing.Y));

            var after = Order(model);
            Assert.NotEqual(before, after);
            Assert.True(Array.IndexOf(after, "shell.tab.plugins") > Array.IndexOf(after, "shell.tab.history"),
                $"Plugins should have landed after History. Order is {string.Join(", ", after)}.");
        }
        finally
        {
            model.Shutdown();
            window?.Close();
            Settle();
        }
    }

    [AvaloniaFact]
    public void A_press_that_barely_moves_selects_the_tab_and_reorders_nothing()
    {
        var model = Build();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = model };
            model.Window = window;
            window.Show();
            Settle();

            var before = Order(model);
            var log = model.Strip.Items.OfType<TabViewModel>().First(t => t.Key == "shell.tab.log");
            var at = Centre(window, Button(window, log));

            // Two pixels: a hand, not an intention. Under the six the strip asks for.
            window.MouseDown(at, MouseButton.Left);
            Settle();
            window.MouseMove(new Point(at.X + 2, at.Y));
            Settle();
            window.MouseUp(new Point(at.X + 2, at.Y), MouseButton.Left);
            Settle();
            Settle();

            Assert.Equal(before, Order(model));
            Assert.Equal("shell.tab.log", model.SelectedTab?.Key);
        }
        finally
        {
            model.Shutdown();
            window?.Close();
            Settle();
        }
    }

    [AvaloniaFact]
    public void A_drag_that_ends_where_it_started_still_does_not_count_as_a_click()
    {
        // Out and back, letting go on the very button the press began on. Avalonia raises Click
        // when a press and its release are on the same control, so this is the case where only
        // swallowing the release keeps a finished drag from also selecting something. Ending the
        // drag anywhere else proves nothing, because the press and release are then on different
        // buttons and no Click was ever going to be raised.
        var model = Build();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = model };
            model.Window = window;
            window.Show();
            Settle();

            var home = model.Tabs.First(t => t.Key == "shell.tab.home");
            model.SelectedTab = home;
            Settle();

            var before = Order(model);
            var plugins = model.Strip.Items.OfType<TabViewModel>().First(t => t.Key == "shell.tab.plugins");
            var at = Centre(window, Button(window, plugins));

            window.MouseDown(at, MouseButton.Left);
            Settle();
            window.MouseMove(new Point(at.X + 40, at.Y));
            Settle();
            window.MouseMove(at);
            Settle();
            window.MouseUp(at, MouseButton.Left);
            Settle();
            Settle();

            Assert.Equal("shell.tab.home", model.SelectedTab?.Key);
            Assert.Equal(before, Order(model));
        }
        finally
        {
            model.Shutdown();
            window?.Close();
            Settle();
        }
    }

    [AvaloniaFact]
    public void The_marker_shows_while_dragging_and_is_gone_afterwards()
    {
        var model = Build();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = model };
            model.Window = window;
            window.Show();
            Settle();

            var first = model.Strip.Items.OfType<TabViewModel>().First(t => t.Key == "shell.tab.plugins");
            var third = model.Strip.Items.OfType<TabViewModel>().First(t => t.Key == "shell.tab.history");
            var landing = Centre(window, Button(window, third));

            window.MouseDown(Centre(window, Button(window, first)), MouseButton.Left);
            Settle();
            window.MouseMove(new Point(landing.X + 12, landing.Y));
            Settle();

            var hinted = model.Strip.Items.OfType<TabViewModel>().Count(t => t.Hint != DropHint.None)
                         + model.Strip.Items.OfType<GroupChipViewModel>().Count(c => c.Hint != DropHint.None);
            Assert.Equal(1, hinted);

            window.MouseUp(new Point(landing.X + 12, landing.Y), MouseButton.Left);
            Settle();
            Settle();

            Assert.All(model.Strip.Items.OfType<TabViewModel>(), t => Assert.Equal(DropHint.None, t.Hint));
            Assert.All(model.Strip.Items.OfType<GroupChipViewModel>(), c => Assert.Equal(DropHint.None, c.Hint));
        }
        finally
        {
            model.Shutdown();
            window?.Close();
            Settle();
        }
    }

    [AvaloniaFact]
    public void The_tab_size_setting_reaches_the_buttons()
    {
        // The size is a style class on the strip rather than a number bound into each element,
        // which is tidy and is also exactly the kind of thing that binds to nothing and fails
        // silently. This asserts the pixels, not the preference.
        var model = Build();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = model };
            model.Window = window;
            window.Show();
            Settle();

            var tab = model.Strip.Items.OfType<TabViewModel>().First();
            Assert.Equal(13d, Button(window, tab).FontSize);

            model.Settings!.SetTabSize(TabSizes.Large);
            Settle();
            Settle();
            Assert.Equal(16d, Button(window, model.Strip.Items.OfType<TabViewModel>().First()).FontSize);

            model.Settings!.SetTabSize(TabSizes.Compact);
            Settle();
            Settle();
            Assert.Equal(11d, Button(window, model.Strip.Items.OfType<TabViewModel>().First()).FontSize);

            // Anything unrecognised reads as the middle one rather than throwing.
            model.Settings!.SetTabSize("enormous");
            Settle();
            Settle();
            Assert.Equal(13d, Button(window, model.Strip.Items.OfType<TabViewModel>().First()).FontSize);
        }
        finally
        {
            model.Shutdown();
            window?.Close();
            Settle();
        }
    }

    [AvaloniaFact]
    public void A_group_chip_shuts_and_opens_its_group_when_clicked()
    {
        var model = Build();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = model };
            model.Window = window;
            window.Show();
            Settle();

            var chip = model.Strip.Items.OfType<GroupChipViewModel>().First();
            var button = window.GetVisualDescendants()
                .OfType<Button>()
                .First(b => b.DataContext is GroupChipViewModel && b.Classes.Contains("chip"));
            var at = Centre(window, button);

            var open = model.Strip.Items.OfType<TabViewModel>().Count();

            window.MouseDown(at, MouseButton.Left);
            Settle();
            window.MouseUp(at, MouseButton.Left);
            Settle();
            Settle();

            Assert.True(model.Strip.Items.OfType<TabViewModel>().Count() < open,
                "Shutting the group should take its tabs off the strip.");
            Assert.True(model.Strip.Groups.First(g => g.Key == chip.Key).IsCollapsed);
        }
        finally
        {
            model.Shutdown();
            window?.Close();
            Settle();
        }
    }
}
