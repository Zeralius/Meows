using System.Net;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Meows.Plugins;
using Meows.Plugins.Abstractions;
using Meows.Services;
using Meows.ViewModels;
using Meows.Views;

namespace Meows.Tests;

/// <summary>
/// The shell's own window, built for real over a throwaway settings folder: Home, Plugins,
/// Settings, History and Log in both themes and both languages, every tab in turn, and a tab
/// into a window of its own and back. The plugin views have had this since 2.x; the shell's
/// were only ever looked at.
/// </summary>
public sealed class ShellViewSmokeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-shell-" + Guid.NewGuid().ToString("N")[..10]);

    public void Dispose()
    {
        PluginNames.Feline = true;
        TestStrings.Install();
        try { Directory.Delete(_root, recursive: true); } catch (Exception) { }
    }

    /// <summary>Answers every request with 404, so the update check runs its course without a network.</summary>
    private sealed class NoInternet : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotFound));
    }

    private (MainWindowViewModel Model, ShellSettings Settings) Build(Action<ShellPreferences>? before = null)
    {
        Directory.CreateDirectory(_root);
        var log = new ShellLog(Path.Combine(_root, "meows.log"));
        var settings = new ShellSettings(_root, Path.Combine(_root, "no-mews"));
        if (before is not null)
        {
            var saved = settings.LoadPreferences();
            before(saved);
            settings.SavePreferences(saved);
        }
        var text = TestStrings.Load();
        MeowsText.Use(text);
        var notifications = new NotificationCenter();
        var background = new BackgroundTaskService(notifications, log);
        var store = new MeowsStore(_root, _ => { });
        var updater = new PluginUpdates("0.0.0-test", new HttpClient(new NoInternet()));
        var model = new MainWindowViewModel(new PluginCatalog(log), settings, log, notifications, background, text,
            settings.LoadPreferences(), store, updater);
        model.Initialize();
        return (model, settings);
    }

    [AvaloniaFact]
    public void The_window_holds_together_on_every_tab_in_both_themes_and_both_languages()
    {
        var complaints = new BindingComplaints(Logger.Sink);
        var previousSink = Logger.Sink;
        Logger.Sink = complaints;

        var (model, _) = Build();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = model };
            model.Window = window;
            window.Show();
            Settle();

            Assert.Equal("shell.tab.home", model.Tabs[0].Key);
            Assert.Equal("shell.tab.plugins", model.Tabs[1].Key);

            foreach (var tab in model.Tabs.ToList())
            {
                model.SelectedTab = tab;
                Settle();
            }

            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Settle();
            }

            var text = TestStrings.Load();
            MeowsText.Use(text);
            text.Use("de");
            Settle();
            text.Use("en");
            Settle();
        }
        finally
        {
            model.Shutdown();
            window?.Close();
            Settle();
            Logger.Sink = previousSink;
        }

        Assert.True(complaints.Lines.Count == 0,
            $"The shell raised {complaints.Lines.Count} complaint(s):\n" + string.Join("\n", complaints.Lines.Distinct()));
    }

    [AvaloniaFact]
    public void A_tab_goes_into_its_own_window_and_comes_back()
    {
        var (model, settings) = Build();
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = model };
            model.Window = window;
            window.Show();
            Settle();

            var history = model.Tabs.Single(t => t.Key == "shell.tab.history");
            var content = (Control)history.Content;

            model.PopOutCommand.Execute(history);
            Settle();
            Settle();

            Assert.True(history.IsPoppedOut);
            Assert.IsType<PoppedOutNotice>(history.Shown);
            var own = TopLevel.GetTopLevel(content) as Window;
            Assert.NotNull(own);
            Assert.NotSame(window, own);
            Assert.Contains("shell.tab.history", settings.LoadPreferences().PoppedOutTabs);

            model.BringBackCommand.Execute(history);
            Settle();
            Settle();

            Assert.False(history.IsPoppedOut);
            Assert.Same(content, history.Shown);
            Assert.Empty(settings.LoadPreferences().PoppedOutTabs);
        }
        finally
        {
            model.Shutdown();
            window?.Close();
            Settle();
        }
    }

    [AvaloniaFact]
    public void A_rule_whose_plugins_are_not_installed_is_shown_with_the_reason_and_kept()
    {
        var complaints = new BindingComplaints(Logger.Sink);
        var previousSink = Logger.Sink;
        Logger.Sink = complaints;

        var (model, settings) = Build(p => p.Rules.Add(new InstinctRule
        {
            Source = "meows.birdwatch", Kind = "saved", Target = "meows.portion", Action = "check", Matching = "paws",
        }));
        Window? window = null;
        try
        {
            window = new MainWindow { DataContext = model };
            model.Window = window;
            window.Show();
            model.SelectedTab = model.Tabs.Single(t => t.Key == "shell.tab.rules");
            Settle();

            var rows = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains(rows, t => t is not null && t.Contains("meows.birdwatch") && t.Contains("paws"));
            Assert.Contains(rows, t => t is not null && t.StartsWith("Waiting for meows.birdwatch"));
        }
        finally
        {
            model.Shutdown();
            window?.Close();
            Settle();
            Logger.Sink = previousSink;
        }

        Assert.Single(settings.LoadPreferences().Rules);
        Assert.True(complaints.Lines.Count == 0,
            $"The rules tab raised {complaints.Lines.Count} complaint(s):\n" + string.Join("\n", complaints.Lines.Distinct()));
    }

    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }
}
