using System.Runtime.InteropServices;
using Avalonia;
using Meows.Services;

namespace Meows;

internal static class Program
{
    private const int AttachParentProcess = -1;

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AttachConsole(int processId);

    /// <summary>
    /// Attaches to the console of whatever launched us.
    ///
    /// This is a WinExe, so it has no console of its own and standard output goes nowhere when
    /// run from a terminal. Only matters for --list-plugins, which exists to be read.
    /// </summary>
    private static void UseParentConsole()
    {
        try
        {
            // Skip this if the output is already redirected. Replacing Console.Out after
            // attaching would send everything to the console instead of down the pipe, and the
            // caller gets an empty file. That failed a perfectly good package in CI.
            if (Console.IsOutputRedirected)
                return;

            if (!OperatingSystem.IsWindows() || !AttachConsole(AttachParentProcess))
                return;

            var writer = new StreamWriter(Console.OpenStandardOutput()) { AutoFlush = true };
            Console.SetOut(writer);
        }
        catch (Exception)
        {
            // No console to attach to. Output goes wherever it was already going.
        }
    }

    [STAThread]
    public static void Main(string[] args)
    {
        // First thing, so startup failures get recorded too. The log lives with the settings
        // rather than next to the exe, since the unzipped folder may not be writable.
        var log = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
            "Meows", "meows.log");

        CrashLog.Watch(log);

        // Discovery without a window, so the release build can check the package it just made
        // actually loads. Checking the files exist is not the same: a plugin missing a
        // dependency is present and still fails, which is how three once shipped broken.
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
            // Rethrow after logging so the exit code still reports the failure.
            CrashLog.Write("fatal", ex);
            throw;
        }
    }

    /// <summary>
    /// Prints what the shell can find and load, one per line, and returns non zero if anything
    /// was refused. Loading constructs the plugin but never creates its view, so no display is
    /// needed.
    /// </summary>
    private static int ListPlugins()
    {
        try
        {
            var log = new ShellLog(Path.Combine(Path.GetTempPath(), "meows-list-plugins.log"));
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
    /// Private libraries a plugin references that are not sitting beside it.
    ///
    /// Constructing the plugin does not catch these: the plugin class rarely touches the shared
    /// library, but the view model built later does. So a plugin with a missing dependency loads
    /// fine and fails the moment it is switched on, which is how three shipped broken.
    ///
    /// Assemblies the shell shares are skipped, since those resolve from the shell.
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
            if (name is null || !name.StartsWith("Meows.", StringComparison.OrdinalIgnoreCase))
                continue;

            // Shared with the shell on purpose, so it should not be in the plugin folder.
            if (name.Equals("Meows.Plugins.Abstractions", StringComparison.OrdinalIgnoreCase))
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
