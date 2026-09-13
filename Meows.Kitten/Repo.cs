namespace Meows.Kitten;

/// <summary>The checkout, found by walking up from wherever Kitten was started until Meows.sln appears.</summary>
public sealed record Repo(string Root)
{
    public string Solution => Path.Combine(Root, "Meows.sln");

    public string TestsProject => Path.Combine(Root, "Meows.Tests", "Meows.Tests.csproj");

    public string ShippedPlugins => Path.Combine(Root, "Meows.Tests", "ShippedPlugins.cs");

    public string Readme => Path.Combine(Root, "README.md");

    public string ShellProject => Path.Combine(Root, "Meows", "Meows.csproj");

    /// <summary>Gitignored, and present only in a checkout that keeps one. Kitten writes to it when it is there.</summary>
    public string Changelog => Path.Combine(Root, "CHANGELOG.md");

    public static Repo? Find(string from)
    {
        for (var dir = new DirectoryInfo(from); dir is not null; dir = dir.Parent)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Meows.sln")))
                return new Repo(dir.FullName);
        }

        return null;
    }

    public string Relative(string path) => Path.GetRelativePath(Root, path).Replace('\\', '/');
}
