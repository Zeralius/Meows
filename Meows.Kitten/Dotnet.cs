using System.Diagnostics;

namespace Meows.Kitten;

/// <summary>One dotnet invocation, its output kept so a failure can be shown whole.</summary>
public sealed record DotnetResult(int ExitCode, string Output);

public static class Dotnet
{
    public static DotnetResult Run(string workingDirectory, params string[] arguments)
    {
        var start = new ProcessStartInfo("dotnet")
        {
            WorkingDirectory = workingDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach (var argument in arguments)
            start.ArgumentList.Add(argument);

        using var process = Process.Start(start)!;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        process.WaitForExit();

        return new DotnetResult(process.ExitCode, (output.Result + error.Result).Trim());
    }
}

/// <summary>
/// The part that keeps Kitten honest. The new project is built, then the test project, then the
/// tests that walk every shipped plugin: catalogue, view smoke, plain names, search, and the new
/// plugin's own. If the template has drifted from what the shell expects, this is where it says
/// so, in the same terminal, before anything is committed.
/// </summary>
public static class Verify
{
    public static string? Run(Repo repo, string name)
    {
        var project = Path.Combine(repo.Root, $"Meows.Plugins.{name}", $"Meows.Plugins.{name}.csproj");

        Console.WriteLine($"  dotnet build {repo.Relative(project)}");
        var plugin = Dotnet.Run(repo.Root, "build", project, "--nologo", "-v", "q");
        if (plugin.ExitCode != 0)
            return $"The new plugin does not build:\n{plugin.Output}";

        Console.WriteLine("  dotnet build Meows.Tests");
        var tests = Dotnet.Run(repo.Root, "build", repo.TestsProject, "--nologo", "-v", "q");
        if (tests.ExitCode != 0)
            return $"The test project does not build with the new plugin in it:\n{tests.Output}";

        var filter = string.Join("|",
            $"FullyQualifiedName~{name}Tests",
            "FullyQualifiedName~ViewSmokeTests",
            "FullyQualifiedName~CatalogueTests",
            "FullyQualifiedName~PluginNamesTests",
            "FullyQualifiedName~SearchTests");

        Console.WriteLine("  dotnet test Meows.Tests, the tests that know every plugin");
        var run = Dotnet.Run(repo.Root, "test", repo.TestsProject, "--no-build", "--nologo", "--filter", filter);
        if (run.ExitCode != 0)
            return $"The plugin-wide tests fail with the new plugin in the list:\n{run.Output}";

        return null;
    }
}
