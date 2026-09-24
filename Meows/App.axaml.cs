using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Meows.Plugins;
using Meows.Plugins.Abstractions;
using Meows.Services;
using Meows.ViewModels;
using Meows.Views;

namespace Meows;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = new ShellSettings();
            // Where the settings are, for code that cannot be handed a host: Meows.Bot.Core's
            // shared bot folder lives beside them, and a portable Meows must not leave bot.json
            // in the profile. An environment variable crosses the plugin load contexts; a
            // static on a shared library would not, since each plugin carries its own copy.
            Environment.SetEnvironmentVariable(ShellSettings.RootVariable, settings.Root);
            var log = new ShellLog(Path.Combine(settings.Root, "meows.log"));
            settings.Report = message => log.Write("settings", message);

            if (settings.StartupNote is { } note)
                log.Write("settings", note);

            // Both of these before the first control exists. The theme has to be right on the
            // first frame rather than flashing dark and correcting itself, and the strings have
            // to be there before anything asks for one.
            var preferences = settings.LoadPreferences();
            RequestedThemeVariant = Appearance.VariantFor(preferences.Theme);

            var text = new Translations(message => log.Write("strings", message));
            text.Add(typeof(App).Assembly);
            text.Use(preferences.Language);
            MeowsText.Use(text);

            var notifications = new NotificationCenter();
            var background = new BackgroundTaskService(notifications, log);
            var catalog = new PluginCatalog(log);
            var store = new MeowsStore(settings.Root, message => log.Write("store", message));
            var viewModel = new MainWindowViewModel(
                catalog, settings, log, notifications, background, text, preferences, store);

            viewModel.Initialize();

            // The history rule, once now and daily from here on. Reads the preference each time,
            // so a change on the Settings tab takes effect at the next pass without a restart.
            var keeper = new HistoryKeeper(store, () => preferences.HistoryKeepDays, log, background, text);
            keeper.Apply();
            desktop.ShutdownRequested += (_, _) => keeper.Dispose();

            // Meows lives in the tray from here on. The window is a view of it, opened on demand,
            // and closing the window hides it unless the Settings tab says a close is a quit. Started
            // with --tray, as the login entry does when asked, there is no window until it is wanted.
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            var tray = new TrayPresence(
                desktop,
                () => new MainWindow { DataContext = viewModel },
                preferences,
                notifications,
                text,
                viewModel.Shutdown,
                background);

            desktop.ShutdownRequested += (_, _) => tray.Dispose();

            // Saying it outside the window: a one-off event becomes a Windows notification while
            // the window is not in front. A condition does not, since a state true all week
            // should not announce itself every pass. The surface is decided once, here.
            var surface = Toasts.Prepare(ShellSettings.IsPortable, message => log.Write("toast", message));
            log.Write("toast", $"Saying it outside the window as: {surface}");
            var buttons = new ToastButtons();
            notifications.Posted += item =>
            {
                if (!preferences.SayOutside || tray.IsWindowActive)
                    return;
                var pressable = item.Actions
                    .Select(action => (action.Label, buttons.Remember(() =>
                    {
                        action.Invoke();
                        if (action.DismissesAfter)
                            notifications.Dismiss(item);
                    })))
                    .ToList();
                Toasts.Show($"{item.SourceName} · {item.Title}", item.Message,
                    item.Severity >= Meows.Plugins.Abstractions.NotificationSeverity.Warning,
                    message => log.Write("toast", message), pressable);
            };

            // The weekly recap: checked hourly, posted once a week from the week's history. The
            // first week only starts the clock, since a recap of the week before is a recap of nothing.
            if (preferences.LastRecapUtc is null)
            {
                preferences.LastRecapUtc = DateTime.UtcNow;
                settings.SavePreferences(preferences);
            }
            background.ScheduleForShell(text["recap.task"], TimeSpan.FromHours(1), _ =>
            {
                var now = DateTime.UtcNow;
                if (preferences.WeeklyRecap && WeeklyRecap.IsDue(preferences.LastRecapUtc, now))
                {
                    var recap = WeeklyRecap.Of(store.Between(now - WeeklyRecap.Week, now), now - WeeklyRecap.Week, now);
                    preferences.LastRecapUtc = now;
                    settings.SavePreferences(preferences);
                    notifications.Post("Meows", Meows.Plugins.Abstractions.NotificationSeverity.Info,
                        text["recap.title"], WeeklyRecap.Summary(recap, text), []);
                }
                return Task.CompletedTask;
            }, runImmediately: true);

            // A second Meows started while this one runs: its arguments arrive here, and the
            // window comes up unless that start only wanted the tray.
            Program.Instance?.Listen(args => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                log.Write("shell", "Another start asked for the running Meows" +
                    (args.Length == 0 ? "." : ": " + string.Join(' ', args)));
                // A toast's button: pressed here, without bringing the window up for it.
                if (args.Count(buttons.Press) > 0)
                    return;
                if (!args.Contains(StartWithWindows.TrayArgument, StringComparer.OrdinalIgnoreCase))
                    tray.Show();
            }));
            desktop.ShutdownRequested += (_, _) => Program.Instance?.Dispose();

            var startHidden = desktop.Args?.Contains(StartWithWindows.TrayArgument, StringComparer.OrdinalIgnoreCase) == true;
            if (!startHidden)
                tray.Show();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
