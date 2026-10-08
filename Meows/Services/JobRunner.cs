using System.Text.Json;
using Meows.Plugins;
using Meows.Plugins.Abstractions;

namespace Meows.Services;

/// <summary>
/// When each job was last run from outside, <c>plugin.job</c> to the time, in the settings
/// folder. It is what lets a plugin's own schedule stand down while Task Scheduler does the work.
/// </summary>
public static class OutsideRuns
{
    /// <summary>How long a run from outside counts as Windows owning the job: a week and a day, so a weekly task still counts.</summary>
    public static readonly TimeSpan Fresh = TimeSpan.FromDays(8);

    private static string FileIn(string root) => Path.Combine(root, "jobs-from-outside.json");

    public static IReadOnlyDictionary<string, DateTime> Load(string root)
    {
        try
        {
            var file = FileIn(root);
            return File.Exists(file)
                ? JsonSerializer.Deserialize<Dictionary<string, DateTime>>(File.ReadAllText(file)) ?? []
                : [];
        }
        catch (Exception)
        {
            return new Dictionary<string, DateTime>();
        }
    }

    public static void Stamp(string root, string pluginId, string jobId, DateTime whenUtc)
    {
        var all = new Dictionary<string, DateTime>(Load(root)) { [$"{pluginId}.{jobId}"] = whenUtc };
        Directory.CreateDirectory(root);
        var file = FileIn(root);
        var temporary = file + ".tmp";
        File.WriteAllText(temporary, JsonSerializer.Serialize(all, new JsonSerializerOptions { WriteIndented = true }));
        File.Move(temporary, file, overwrite: true);
    }

    public static bool IsFresh(string root, string pluginId, string jobId, DateTime nowUtc) =>
        Load(root).TryGetValue($"{pluginId}.{jobId}", out var when) && nowUtc - when < Fresh;
}

/// <summary>What a job is given: the dormant host's reading, plus saving, a log, progress and a notification.</summary>
public sealed class JobHost(string pluginId, ShellSettings settings, IMeowsStore store, ShellLog log, Action<string> report, Action<string, string, bool> notify)
    : IMeowsJobHost
{
    public string PluginId { get; } = pluginId;

    public string DataDirectory { get; } = settings.PluginDataDirectory(pluginId);

    public IMeowsText Text => MeowsText.Current;

    public IMeowsStore Store { get; } = store;

    public IMeowsHandoff Handoff => NoHandoff.Instance;

    public T? LoadSettings<T>() where T : class => settings.LoadPluginSettings<T>(PluginId);

    public void SaveSettings<T>(T value) where T : class => settings.SavePluginSettings(PluginId, value);

    public void Log(string message) => log.Write(PluginId, message);

    public void Report(string status) => report(status);

    public void Notify(string title, string text, bool isTrouble = false)
    {
        log.Write(PluginId, $"{(isTrouble ? "trouble" : "note")}: {title}: {text}");
        notify(title, text, isTrouble);
    }
}

/// <summary>How a <c>--do</c> ended, with the exit code a scheduled task will see.</summary>
public sealed record JobResult(int ExitCode, string Said);

/// <summary>
/// <c>Meows.exe --do weighin.measure</c>: one plugin's job, in this process, with no window.
/// Only for a plugin that is switched on, since off means off whoever is asking. The run is
/// stamped so the plugin's own schedule, if it has one for the same work, stands down.
/// </summary>
public static class JobRunner
{
    public const int Done = 0;
    public const int Failed = 1;
    public const int Unknown = 2;
    public const int SwitchedOff = 3;

    /// <summary>"weighin.measure" or "meows.weighin.measure": the plugin and the job, the last dot between them.</summary>
    public static (string PluginId, string JobId)? Parse(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || dot == name.Length - 1)
            return null;
        var plugin = name[..dot];
        if (!plugin.Contains('.'))
            plugin = "meows." + plugin;
        return (plugin.ToLowerInvariant(), name[(dot + 1)..].ToLowerInvariant());
    }

    /// <summary>Every job every compatible plugin offers, for <c>--do</c> with nothing after it.</summary>
    public static IReadOnlyList<(PluginDescriptor Plugin, PluginJob Job)> All(IEnumerable<PluginDescriptor> found) =>
        found.Where(d => d.IsCompatible && d.Plugin is not null)
            .SelectMany(d => SafeJobs(d).Select(j => (d, j)))
            .OrderBy(p => p.d.Id, StringComparer.Ordinal)
            .ThenBy(p => p.j.Id, StringComparer.Ordinal)
            .ToList();

    private static IReadOnlyList<PluginJob> SafeJobs(PluginDescriptor descriptor)
    {
        try
        {
            return descriptor.Plugin!.Jobs;
        }
        catch (Exception)
        {
            return [];
        }
    }

    /// <summary>The name a job goes by on the command line: the plugin id without "meows.", a dot, the job.</summary>
    public static string NameOf(PluginDescriptor plugin, PluginJob job) =>
        (plugin.Id.StartsWith("meows.", StringComparison.Ordinal) ? plugin.Id["meows.".Length..] : plugin.Id) + "." + job.Id;

    public static async Task<JobResult> RunAsync(
        IEnumerable<PluginDescriptor> found,
        IReadOnlySet<string> switchedOn,
        string name,
        Func<PluginDescriptor, IMeowsJobHost> hostFor,
        string settingsRoot,
        CancellationToken token)
    {
        var text = MeowsText.Current;
        if (Parse(name) is not var (pluginId, jobId))
            return new JobResult(Unknown, text.Format("do.unknown", name));

        var plugin = found.FirstOrDefault(d => d.IsCompatible && string.Equals(d.Id, pluginId, StringComparison.OrdinalIgnoreCase));
        var job = plugin is null ? null : SafeJobs(plugin).FirstOrDefault(j => string.Equals(j.Id, jobId, StringComparison.OrdinalIgnoreCase));
        if (plugin is null || job is null)
            return new JobResult(Unknown, text.Format("do.unknown", name));
        if (!switchedOn.Contains(plugin.Id))
            return new JobResult(SwitchedOff, text.Format("do.off", plugin.Name));

        OutsideRuns.Stamp(settingsRoot, plugin.Id, job.Id, DateTime.UtcNow);
        try
        {
            var said = await plugin.Plugin!.RunJob(job.Id, hostFor(plugin), token).ConfigureAwait(false);
            return new JobResult(Done, said);
        }
        catch (JobDeclinedException declined)
        {
            return new JobResult(Failed, declined.Message);
        }
        catch (OperationCanceledException)
        {
            return new JobResult(Failed, text["do.stopped"]);
        }
        catch (Exception ex)
        {
            return new JobResult(Failed, text.Format("do.failed", ex.Message));
        }
    }
}
