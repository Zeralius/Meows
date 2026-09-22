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

            // A second Meows started while this one runs: its arguments arrive here, and the
            // window comes up unless that start only wanted the tray.
            Program.Instance?.Listen(args => Avalonia.Threading.Dispatcher.UIThread.Post(() =>
            {
                log.Write("shell", "Another start asked for the running Meows" +
                    (args.Length == 0 ? "." : ": " + string.Join(' ', args)));
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
