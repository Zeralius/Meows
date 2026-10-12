using System.Text.Json;

namespace Meows.Tests;

/// <summary>
/// Every string catalogue kept in key order, which is what lets a missing or doubled key be
/// seen in a diff, and what the pull request template asks for.
/// </summary>
public sealed class CatalogueOrderTests
{
    private static string Repo([System.Runtime.CompilerServices.CallerFilePath] string here = "") =>
        Path.GetDirectoryName(Path.GetDirectoryName(here))!;

    [Fact]
    public void Every_catalogue_is_in_key_order()
    {
        var sep = Path.DirectorySeparatorChar;
        var offenders = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Repo(), "Strings.*.json", SearchOption.AllDirectories)
                     .Where(f => !f.Contains($"{sep}bin{sep}") && !f.Contains($"{sep}obj{sep}") &&
                                 !f.Contains($"{sep}Meows.Tests{sep}") && !f.Contains($"{sep}plugins{sep}")))
        {
            using var document = JsonDocument.Parse(File.ReadAllText(file));
            var keys = document.RootElement.EnumerateObject().Select(p => p.Name).ToList();
            var sorted = keys.Order(StringComparer.Ordinal).ToList();
            if (!keys.SequenceEqual(sorted))
                offenders.Add(Path.GetRelativePath(Repo(), file));
        }

        Assert.Empty(offenders);
    }
}
