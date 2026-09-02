using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Mews.Plugins;
using Mews.Services;
using Mews.ViewModels;
using Mews.Views;

namespace Mews;

public partial class App : Application
{
    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var settings = new ShellSettings();
            var log = new ShellLog(Path.Combine(settings.Root, "mews.log"));
            settings.Report = message => log.Write("settings", message);
            var notifications = new NotificationCenter();
            var background = new BackgroundTaskService(notifications, log);
            var catalog = new PluginCatalog(log);
            var viewModel = new MainWindowViewModel(catalog, settings, log, notifications, background);

            desktop.MainWindow = new MainWindow { DataContext = viewModel };
            desktop.ShutdownRequested += (_, _) => viewModel.Shutdown();

            viewModel.Initialize();
        }

        base.OnFrameworkInitializationCompleted();
    }
}
