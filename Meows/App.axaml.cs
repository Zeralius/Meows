using Avalonia;
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
            var viewModel = new MainWindowViewModel(
                catalog, settings, log, notifications, background, text, preferences);

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.ShutdownRequested += (_, _) => viewModel.Shutdown();

            viewModel.Initialize();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
