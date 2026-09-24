using Meows.Plugins.Mouser.Services;
using Meows.Plugins.Mouser.ViewModels;

namespace Meows.Tests;

/// <summary>
/// Shadow: a file is an orphan only against the folder it sits in, each rule says what it was
/// expecting, a rule that is off says nothing, and when a rule cannot be sure the file stays.
/// </summary>
public sealed class ShadowTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "shadow-" + Guid.NewGuid().ToString("N")[..10]);

    public ShadowTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (Exception)
        {
        }
    }

    private string Touch(params string[] parts)
    {
        var path = Path.Combine([_root, .. parts]);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, [1, 2, 3]);
        return path;
    }

    private IReadOnlyList<Finding> Orphans(IReadOnlySet<string>? rules = null) =>
        MouserScan.Run(_root, new MouserOptions { SkipSystemFolders = false, ShadowRules = rules ?? new HashSet<string>(Shadow.DefaultOn) }, null, CancellationToken.None)
            .Findings.Where(f => f.Kind == DeadKind.Orphan).ToList();

    private static string[] Names(IEnumerable<Finding> findings) => findings.Select(f => f.Name).Order(StringComparer.Ordinal).ToArray();

    [Fact]
    public void Subtitles_belong_to_a_video_by_name_to_the_only_video_or_to_the_film_one_folder_up()
    {
        Touch("films", "a.mkv");
        Touch("films", "a.en.srt");
        Touch("films", "b.mp4");
        Touch("films", "gone.en.forced.srt");
        Touch("one", "movie.2019.mkv");
        Touch("one", "English.srt");
        Touch("film", "Film.mkv");
        Touch("film", "Subs", "2_English.srt");
        Touch("lonely", "talk.vtt");

        var found = Orphans();

        Assert.Equal(["gone.en.forced.srt", "talk.vtt"], Names(found));
        var gone = found.Single(f => f.Name == "gone.en.forced.srt");
        Assert.Equal("mouser.detail.orphan.subtitles", gone.Detail);
        Assert.Equal(["gone"], gone.DetailValues);
    }

    [Fact]
    public void Sidecars_and_download_notes_want_what_they_describe()
    {
        Touch("photos", "IMG_1.HEIC");
        Touch("photos", "IMG_1.AAE");
        Touch("photos", "IMG_2.AAE");
        Touch("photos", "raw.cr2");
        Touch("photos", "raw.cr2.xmp");
        Touch("photos", "lost.xmp");
        Touch("takeout", "cat.jpg");
        Touch("takeout", "cat.jpg.json");
        Touch("takeout", "dog.jpg.json");
        Touch("takeout", "settings.json");
        Touch("clips", "clip.webm");
        Touch("clips", "clip.info.json");
        Touch("clips", "other.info.json");

        Assert.Equal(["IMG_2.AAE", "dog.jpg.json", "lost.xmp", "other.info.json"], Names(Orphans()));
    }

    [Fact]
    public void Unity_meta_is_judged_only_inside_a_project_and_gcode_only_when_asked()
    {
        Touch("Game", "Assets", "Hero.png");
        Touch("Game", "Assets", "Hero.png.meta");
        Directory.CreateDirectory(Path.Combine(_root, "Game", "Assets", "Sounds"));
        Touch("Game", "Assets", "Sounds.meta");
        Touch("Game", "Assets", "Deleted.fbx.meta");
        Touch("elsewhere", "save.meta");
        Touch("prints", "benchy.stl");
        Touch("prints", "benchy_0.2mm_PLA.gcode");
        Touch("prints", "vase.gcode");

        Assert.Equal(["Deleted.fbx.meta"], Names(Orphans()));

        var all = new HashSet<string>(Shadow.Rules.Select(r => r.Id));
        Assert.Equal(["Deleted.fbx.meta", "vase.gcode"], Names(Orphans(all)));
        Assert.Empty(Orphans(new HashSet<string>()));
    }

    [Fact]
    public void Switching_a_rule_off_is_remembered_and_the_default_leaves_gcode_off()
    {
        var host = new FakeHost(Path.Combine(_root, "host"));
        using (var model = new MouserViewModel(host))
        {
            Assert.False(model.ShadowRules.Single(r => r.Rule == Shadow.Gcode).IsOn);
            Assert.True(model.ShadowRules.Single(r => r.Rule == Shadow.Subtitles).IsOn);
            model.ShadowRules.Single(r => r.Rule == Shadow.Subtitles).IsOn = false;
        }

        using var again = new MouserViewModel(host);
        Assert.False(again.ShadowRules.Single(r => r.Rule == Shadow.Subtitles).IsOn);
        Assert.True(again.ShadowRules.Single(r => r.Rule == Shadow.Sidecar).IsOn);
    }
}
