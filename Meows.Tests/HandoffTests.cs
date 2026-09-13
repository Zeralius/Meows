using Meows.Plugins.Abstractions;
using Meows.Plugins.Chonk.Services;
using Meows.Plugins.Chonk.ViewModels;
using Meows.Plugins.Kibble.ViewModels;
using Meows.Plugins.Purrge.ViewModels;
using Meows.Plugins.Scruff.ViewModels;

namespace Meows.Tests;

/// <summary>
/// One plugin handing work to another: the sender asks the host, the receiver is asked whether
/// it takes the shape, and then it is given it. The shell's routing in between is a dictionary
/// lookup and a tab change; what is worth testing is what each end does.
/// </summary>
public class HandoffTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-handoff-" + Guid.NewGuid().ToString("N"));

    public HandoffTests()
    {
        Directory.CreateDirectory(Path.Combine(_root, "fat"));
        File.WriteAllBytes(Path.Combine(_root, "fat", "big.bin"), new byte[4096]);
        File.WriteAllBytes(Path.Combine(_root, "fat", "pic.jpg"), TestPictures.Jpeg());
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private FakeHost Host(string name, params string[] reachable)
    {
        var host = new FakeHost(Path.Combine(_root, "host-" + name));
        foreach (var id in reachable)
            host.Handoffs.Reachable.Add(id);
        return host;
    }

    [Fact]
    public void Chonk_offers_the_other_tools_only_when_they_are_installed()
    {
        using var alone = new ChonkViewModel(Host("alone"));
        using var together = new ChonkViewModel(Host("together", KnownPlugins.Purrge, KnownPlugins.Kibble));

        Assert.False(alone.CanReachPurrge);
        Assert.False(alone.CanReachKibble);
        Assert.True(together.CanReachPurrge);
        Assert.True(together.CanReachKibble);
    }

    [Fact]
    public void Chonk_hands_the_selected_folder_to_purrge()
    {
        var host = Host("chonk", KnownPlugins.Purrge);
        using var model = new ChonkViewModel(host);
        model.ShowScanned(DiskScan.Run(_root, new ScanOptions(), null, CancellationToken.None));
        model.Selected = model.Entries.First(e => e.Name == "fat");

        Assert.True(model.FindDuplicatesCommand.CanExecute(null));
        model.FindDuplicatesCommand.Execute(null);

        var (to, what) = Assert.Single(host.Handoffs.Sent);
        Assert.Equal(KnownPlugins.Purrge, to);
        Assert.Equal(HandoffVerbs.Folder, what.Verb);
        Assert.Equal(Path.Combine(_root, "fat"), Assert.Single(what.Paths));
    }

    [Fact]
    public void Purrge_takes_a_folder_and_starts_scanning_it()
    {
        var host = Host("purrge");
        using var model = new PurrgeViewModel(host);
        var handoff = Handoff.Folder(Path.Combine(_root, "fat"));

        Assert.True(model.Accepts(handoff));
        Assert.False(model.Accepts(Handoff.Files([Path.Combine(_root, "fat", "pic.jpg")])));

        model.Receive(handoff);

        Assert.Equal(Path.Combine(_root, "fat"), model.ScanRoot);
        Assert.False(model.IsCompareMode);
        Assert.Contains(host.Work.Requested, title => title.Contains("fat"));
    }

    [Fact]
    public void Kibble_takes_a_folder_and_opens_it()
    {
        var host = Host("kibble");
        using var model = new KibbleViewModel(host);

        model.Receive(Handoff.Folder(Path.Combine(_root, "fat")));

        Assert.Equal(Path.Combine(_root, "fat"), model.SourceFolder);
    }

    [Fact]
    public void Scruff_takes_files_onto_the_pile()
    {
        var host = Host("scruff");
        using var model = new ScruffViewModel(host, new HttpClient());
        var handoff = Handoff.Files([Path.Combine(_root, "fat", "pic.jpg")]);

        Assert.True(model.Accepts(handoff));
        model.Receive(handoff);

        Assert.Equal("pic.jpg", Assert.Single(model.Files).Name);
    }

    [Fact]
    public void A_shell_without_handoffs_reaches_nobody_and_a_send_is_a_quiet_no()
    {
        IMeowsHost host = new StaleHost(Path.Combine(_root, "stale"));

        Assert.False(host.Handoff.CanReach(KnownPlugins.Purrge));
        Assert.False(host.Handoff.Send(KnownPlugins.Purrge, Handoff.Folder(_root)));
        Assert.Null(host.Secrets.Get("anything"));
    }

    /// <summary>An IMeowsHost from before 0.5.0: the defaults on the interface are all it has.</summary>
    private sealed class StaleHost(string dataDirectory) : IMeowsHost
    {
        public string PluginId => "meows.stale";

        public string DataDirectory { get; } = dataDirectory;

        public void Log(string message)
        {
        }

        public IMeowsNotifications Notifications => throw new NotSupportedException();

        public IMeowsBackgroundWork Background => throw new NotSupportedException();

        public T? LoadSettings<T>() where T : class => null;

        public void SaveSettings<T>(T settings) where T : class
        {
        }
    }
}
