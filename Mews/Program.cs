using System.Runtime.InteropServices;
using Avalonia;
using Mews.Services;

namespace Mews;

internal static class Program
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    /// <summary>
    /// Borrows the console of whatever launched us.
    ///
    /// This is a WinExe, so it has no console of its own and anything written to standard output
    /// goes nowhere when it is started from a terminal. That matters only for the listing mode,
    /// which exists to be read.
    /// </summary>
    private static void UseParentConsole()
    {
        try
        {
            if (!OperatingSystem.IsWindows() || !AttachConsole(AttachParentProcess))
                return;

            var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(writer);
        }
        catch (Exception)
        {
            // Launched without a console to borrow. The output still reaches a redirect, which
            // is how a build reads it.
        }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        // Before anything else, so a failure while starting up is recorded too. The log lives
        // beside the settings rather than next to the exe, because the folder the app was
        // unzipped into is not reliably writable.
        var log = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Mews", "mews.log");

        CrashLog.Watch(log);

        // Discovery without a window, so a build can check that the package it just made can
        // actually load what is in it. Checking the files are present is not the same thing: a
        // plugin missing a dependency is present and still fails, which is how three plugins
        // once shipped broken in a package that passed every file check.
        if (args.Contains("--list-plugins", StringComparer.OrdinalIgnoreCase))
        {
            UseParentConsole();
            Environment.ExitCode = ListPlugins();
            return;
        }

        try
        {
            BuildAvaloniaApp().StartWithClassicDesktopLifetime(args);
        }
        catch (Exception ex)
        {
            // Rethrown after recording, so the exit code still says it failed and nothing
            // pretends the run was fine.
            CrashLog.Write("fatal", ex);
            throw;
        }
    }

    /// <summary>
    /// Prints what the shell can find and load, one per line, and returns non zero if anything
    /// was refused. Loading a plugin means constructing it, which is where a missing dependency
    /// actually shows up; no view is ever created, so no display is needed.
    /// </summary>
    private static int ListPlugins()
    {
        try
        {
            var log = new ShellLog(Path.Combine(Path.GetTempPath(), "mews-list-plugins.log"));
            var catalog = new Plugins.PluginCatalog(log);
            var found = catalog.Discover();

            Console.WriteLine($"plugins directory: {catalog.PluginsDirectory ?? "(none found)"}");

            foreach (var descriptor in found.OrderBy(d => d.DisplayName, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(descriptor.IsCompatible
                    ? $"ok      {descriptor.Id}  ({descriptor.DisplayName})"
                    : $"REFUSED {descriptor.Id}  {descriptor.IncompatibleReason}");
            }

            var missing = 0;
            foreach (var descriptor in found.Where(d => d.IsCompatible))
            {
                foreach (var gap in MissingDependencies(descriptor.AssemblyPath))
                {
                    Console.WriteLine($"MISSING {descriptor.Id}  needs {gap}, which is not in its folder");
                    missing++;
                }
            }

            var broken = found.Count(d => !d.IsCompatible);
            Console.WriteLine($"{found.Count} found, {broken} refused, {missing} missing a dependency");
            return broken == 0 && missing == 0 ? 0 : 1;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"Listing plugins failed: {ex}");
            return 2;
        }
    }

    /// <summary>
    /// The private libraries a plugin references that are not beside it.
    ///
    /// Constructing a plugin is not enough to find these. The class itself usually references
    /// nothing unusual; it is the view model built later that needs the shared library, so a
    /// plugin whose dependency is missing loads perfectly and then fails the moment somebody
    /// switches it on. That is precisely how three plugins once shipped broken.
    ///
    /// Anything the shell shares is skipped, since those resolve from the shell rather than from
    /// the plugin folder.
    /// </summary>
    private static IEnumerable<string> MissingDependencies(string assemblyPath)
    {
        var folder = Path.GetDirectoryName(assemblyPath);
        if (folder is null)
            yield break;

        System.Reflection.AssemblyName[] referenced;
        try
        {
            referenced = System.Reflection.Assembly.LoadFrom(assemblyPath).GetReferencedAssemblies();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var reference in referenced)
        {
            var name = reference.Name;
            if (name is null || !name.StartsWith("Mews.", StringComparison.OrdinalIgnoreCase))
                continue;

            // Shared with the shell on purpose, so its absence from the plugin folder is correct.
            if (name.Equals("Mews.Plugins.Abstractions", StringComparison.OrdinalIgnoreCase))
                continue;

            if (!File.Exists(Path.Combine(folder, name + ".dll")))
                yield return name;
        }
    }

    public static AppBuilder BuildAvaloniaApp() => AppBuilder.Configure<App>()
        .UsePlatformDetect()
        .WithInterFont()
        .LogToTrace();
}
