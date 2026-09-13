using Meows.Media;
using Meows.Plugins.Scruff.Services;
using Meows.Plugins.Scruff.ViewModels;

namespace Meows.Tests;

/// <summary>
/// The tab's bookkeeping: what it remembers, what it lets through, what it refuses. Reading and
/// posting hop onto the UI thread, and this suite has no dispatcher, so those are checked in
/// the running window rather than here.
/// </summary>
public class ScruffViewModelTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-scruff-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private (FakeHost Host, ScruffViewModel Model) Fresh(ScruffSettings? settings = null)
    {
        var host = new FakeHost(_root);
        if (settings is not null)
            host.SaveSettings(settings);
        return (host, new ScruffViewModel(host, new HttpClient()));
    }

    [Fact]
    public void Six_places_are_offered_and_only_bluesky_starts_ticked()
    {
        var (_, model) = Fresh();
        using var _ = model;

        Assert.Equal(["bluesky", "mastodon", "furaffinity", "x", "instagram", "reddit"], model.Targets.Select(t => t.Id));
        Assert.Equal(["bluesky"], model.Targets.Where(t => t.IsEnabled).Select(t => t.Id));
    }

    [Fact]
    public void Ticking_a_place_is_remembered()
    {
        var (host, model) = Fresh();
        using var _ = model;

        model.Targets.Single(t => t.Id == "furaffinity").IsEnabled = true;

        var saved = host.LoadSettings<ScruffSettings>()!;
        Assert.Contains("furaffinity", saved.EnabledTargets);
    }

    [Fact]
    public void Nothing_can_be_posted_to_a_place_that_needs_a_login_it_has_not_got()
    {
        var (_, model) = Fresh();
        using var _ = model;

        // Bluesky is ticked but not signed in, so there is nowhere ready to go.
        Assert.False(model.PostCommand.CanExecute(null));

        model.Targets.Single(t => t.Id == "x").IsEnabled = true;
        Assert.True(model.PostCommand.CanExecute(null));
    }

    [Fact]
    public void The_draft_is_what_was_typed_with_the_tags_parsed()
    {
        var (_, model) = Fresh(new ScruffSettings { Rating = Rating.Mature });
        using var _ = model;

        model.Title = "Lunch";
        model.Text = "Someone got eaten.";
        model.TagsText = "vore, big cat";

        var draft = model.CurrentDraft();

        Assert.Equal("Lunch", draft.Title);
        Assert.Equal(["vore", "big cat"], draft.Tags);
        Assert.Equal(Rating.Mature, draft.Rating);
    }

    [Fact]
    public void Each_ticked_place_shows_what_it_would_be_sent_as_the_draft_is_typed()
    {
        var (_, model) = Fresh();
        using var _ = model;
        var x = model.Targets.Single(t => t.Id == "x");
        x.IsEnabled = true;

        model.Text = "hello";
        model.TagsText = "cat";

        Assert.Equal("hello\n\n#cat", x.Preview);
        Assert.Equal("11 / 280", x.LengthText);
        Assert.True(x.CanGo);

        model.Text = new string('a', 300);
        Assert.False(x.CanGo);
        Assert.True(x.IsOverLimit);
    }

    [Fact]
    public void Files_go_on_the_pile_once_each_and_a_folder_means_what_is_in_it()
    {
        Directory.CreateDirectory(_root);
        var folder = Path.Combine(_root, "pics");
        Directory.CreateDirectory(folder);
        var a = Path.Combine(folder, "a.jpg");
        var b = Path.Combine(folder, "b.jpg");
        File.WriteAllBytes(a, TestPictures.Jpeg());
        File.WriteAllBytes(b, TestPictures.Jpeg());

        var (host, model) = Fresh();
        using var _ = model;

        model.AddPaths([folder]);
        model.AddPaths([a]);

        Assert.Equal(2, model.Files.Count);
        Assert.Equal(folder, host.LoadSettings<ScruffSettings>()!.LastFolder);
        Assert.NotNull(model.Selected);
    }

    [Fact]
    public void The_output_folder_defaults_under_pictures_and_can_be_moved()
    {
        var (host, model) = Fresh();
        using var _ = model;

        Assert.EndsWith("Scruffed", model.OutputFolder);

        model.SetOutputFolder(Path.Combine(_root, "out"));

        Assert.Equal(Path.Combine(_root, "out"), host.LoadSettings<ScruffSettings>()!.OutputFolder);
    }
}
