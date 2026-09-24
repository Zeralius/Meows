using Meows.Plugins.Abstractions;
using Meows.Plugins.Familiar.ViewModels;
using Meows.Services;
using Meows.ViewModels;

namespace Meows.Tests;

/// <summary>
/// The server from the Settings tab. A folder is tested for real against temp folders. SFTP is
/// tested against a real server only when one is named in the environment, because there is
/// none on a CI runner:
///
///   MEOWS_SFTP_HOST, MEOWS_SFTP_PORT, MEOWS_SFTP_USER, MEOWS_SFTP_KEY (a key file),
///   MEOWS_SFTP_PASSPHRASE (optional), MEOWS_SFTP_ROOT (a folder there that can be written to).
///
/// Without them those tests return at once and pass, the same way the Windows-only tests do
/// off Windows.
/// </summary>
public sealed class ReachTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-reach-" + Guid.NewGuid().ToString("N"));
    private readonly ReachSettings _settings = new();
    private readonly Secrets _secrets = new();
    private readonly List<string> _log = [];

    public ReachTests() => Directory.CreateDirectory(_root);

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

    /// <summary>Kept in memory, which is all a test needs; the real one is sealed with DPAPI.</summary>
    private sealed class Secrets : IMeowsSecrets
    {
        public Dictionary<string, string> Kept { get; } = [];

        public bool Has(string name) => Kept.ContainsKey(name);

        public string? Get(string name) => Kept.GetValueOrDefault(name);

        public void Set(string name, string value) => Kept[name] = value;

        public void Forget(string name) => Kept.Remove(name);
    }

    private ShellReach Reach() => new(() => _settings, _secrets, (line, _) => _log.Add(line));

    /// <summary>A kit-shaped folder: two files at the top, one a level down.</summary>
    private string Kit(string name = "tavern")
    {
        var kit = Path.Combine(_root, "local", name);
        Directory.CreateDirectory(Path.Combine(kit, "maps"));
        File.WriteAllText(Path.Combine(kit, "kit.json"), "{\"title\":\"Tavern\"}");
        File.WriteAllBytes(Path.Combine(kit, "maps", "01 Tavern.webp"), new byte[5000]);
        File.WriteAllText(Path.Combine(kit, "notes.md"), "# Tonight");
        return kit;
    }

    [Theory]
    [InlineData("Data/modules/meows-kit/kits", new[] { "Data", "modules", "meows-kit", "kits" })]
    [InlineData("a\\b//c/", new[] { "a", "b", "c" })]
    [InlineData("", new string[0])]
    public void A_plain_relative_path_is_split_into_its_folders(string path, string[] expected) =>
        Assert.Equal(expected, ReachPaths.Split(path));

    [Theory]
    [InlineData("/etc/passwd")]
    [InlineData("\\\\server\\share")]
    [InlineData("C:\\Windows")]
    [InlineData("kits/../../etc")]
    [InlineData("kits/./x")]
    [InlineData("kits/a|b")]
    public void A_path_that_is_rooted_or_climbs_out_is_refused(string path) =>
        Assert.Null(ReachPaths.Split(path));

    [Fact]
    public void Nothing_set_reaches_nothing_and_says_what_is_missing()
    {
        var reach = Reach();
        Assert.False(reach.IsSet);
        Assert.Null(reach.Where);

        _settings.Kind = ReachKinds.Folder;
        Assert.False(reach.IsSet);
        Assert.Contains("folder", reach.Missing());

        _settings.Folder = @"\\nas\foundry";
        Assert.True(reach.IsSet);
        Assert.Equal(@"\\nas\foundry", reach.Where);

        // SFTP is only usable with an address, a key, and a server key a person trusted.
        _settings.Kind = ReachKinds.Sftp;
        _settings.Host = "nas";
        _settings.User = "dennis";
        _settings.RemoteRoot = "srv/foundry/";
        Assert.Contains("key", reach.Missing());
        _secrets.Set(ShellReach.KeySecret, "-----BEGIN OPENSSH PRIVATE KEY-----");
        Assert.Contains("trust", reach.Missing());
        _settings.HostKey = "abc";
        Assert.True(reach.IsSet);
        Assert.Equal("sftp://dennis@nas/srv/foundry", reach.Where);
        _settings.Port = 2222;
        Assert.Equal("sftp://dennis@nas:2222/srv/foundry", reach.Where);
    }

    [Fact]
    public async Task A_folder_is_copied_under_the_root_with_its_subfolders_and_replaces_what_it_names()
    {
        var server = Path.Combine(_root, "server");
        Directory.CreateDirectory(server);
        _settings.Kind = ReachKinds.Folder;
        _settings.Folder = server;
        var target = Path.Combine(server, "Data", "modules", "meows-kit", "kits", "tavern");
        Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "kit.json"), "old");
        File.WriteAllText(Path.Combine(target, "someone-elses.txt"), "leave me");

        var seen = new List<ReachProgress>();
        var result = await Reach().CopyFolder(Kit(), "Data/modules/meows-kit/kits/tavern", new SyncProgress<ReachProgress>(seen.Add));

        Assert.True(result.Ok, result.Error);
        Assert.Equal(3, result.Files);
        Assert.Equal(target, result.Where);
        Assert.Equal("{\"title\":\"Tavern\"}", File.ReadAllText(Path.Combine(target, "kit.json")));
        Assert.Equal(5000, new FileInfo(Path.Combine(target, "maps", "01 Tavern.webp")).Length);
        Assert.Equal("leave me", File.ReadAllText(Path.Combine(target, "someone-elses.txt")));
        Assert.Empty(Directory.GetFiles(server, "*" + ShellReach.PartSuffix, SearchOption.AllDirectories));
        Assert.Equal([1, 2, 3], seen.Select(p => p.Done));
        Assert.All(seen, p => Assert.Equal(3, p.Total));
    }

    [Fact]
    public async Task A_server_folder_that_is_not_there_a_bad_path_or_a_missing_kit_is_an_answer_not_an_exception()
    {
        _settings.Kind = ReachKinds.Folder;
        _settings.Folder = Path.Combine(_root, "unplugged");
        var reach = Reach();

        var offline = await reach.CopyFolder(Kit(), "kits/tavern");
        Assert.False(offline.Ok);
        Assert.Contains("unplugged", offline.Error);
        Assert.False(Directory.Exists(_settings.Folder));

        Directory.CreateDirectory(_settings.Folder);
        var climbing = await reach.CopyFolder(Kit(), "../outside");
        Assert.False(climbing.Ok);
        Assert.False(Directory.Exists(Path.Combine(_root, "outside")));

        var nothing = await reach.CopyFolder(Path.Combine(_root, "no-such-kit"), "kits/x");
        Assert.False(nothing.Ok);

        Assert.False((await NoReach.Instance.CopyFolder(Kit(), "kits/x")).Ok);
    }

    [Fact]
    public async Task A_copy_called_off_stops()
    {
        _settings.Kind = ReachKinds.Folder;
        _settings.Folder = Path.Combine(_root, "server");
        Directory.CreateDirectory(_settings.Folder);
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Reach().CopyFolder(Kit(), "kits/tavern", token: stop.Token));
    }

    [Fact]
    public async Task The_settings_test_says_whether_a_folder_takes_files()
    {
        _settings.Kind = ReachKinds.Folder;
        _settings.Folder = Path.Combine(_root, "server");
        Assert.False((await Reach().Test()).Ok);

        Directory.CreateDirectory(_settings.Folder);
        var test = await Reach().Test();
        Assert.True(test.Ok, test.Message);
        Assert.Empty(Directory.GetFiles(_settings.Folder));
    }

    [Fact]
    public void Moving_to_another_host_forgets_the_trusted_key_and_a_key_file_is_kept_sealed_not_pointed_at()
    {
        var saves = 0;
        _settings.Kind = ReachKinds.Sftp;
        _settings.Host = "nas";
        _settings.HostKey = "abc";
        var picker = new FakeHost.FakePicker();
        var model = new ServerViewModel(_settings, Reach(), _secrets, picker, () => saves++, _log.Add);

        model.User = "dennis";
        Assert.Equal("abc", _settings.HostKey);
        model.Host = "other-nas";
        Assert.Null(_settings.HostKey);
        Assert.False(model.HasTrustedKey);
        Assert.Equal(2, saves);

        model.Passphrase = "secret phrase";
        Assert.Equal("secret phrase", _secrets.Get(ShellReach.PassphraseSecret));
        model.Passphrase = "";
        Assert.False(_secrets.Has(ShellReach.PassphraseSecret));

        model.IsFolder = true;
        Assert.Equal(ReachKinds.Folder, _settings.Kind);
        Assert.True(model.IsFolder);
        Assert.False(model.IsSftp);
    }

    [Fact]
    public async Task Choosing_a_key_keeps_its_text_and_refuses_the_public_half()
    {
        var picker = new FakeHost.FakePicker();
        var model = new ServerViewModel(_settings, Reach(), _secrets, picker, () => { }, _log.Add);
        var pub = Path.Combine(_root, "id_ed25519.pub");
        File.WriteAllText(pub, "ssh-ed25519 AAAA me@pc");
        var key = Path.Combine(_root, "id_ed25519");
        File.WriteAllText(key, "-----BEGIN OPENSSH PRIVATE KEY-----\nabc\n-----END OPENSSH PRIVATE KEY-----\n");

        picker.Answers.Enqueue(pub);
        model.ChooseKeyCommand.Execute(null);
        await Task.Delay(50);
        Assert.False(model.HasKey);
        Assert.Contains(".pub", model.TestResult);

        picker.Answers.Enqueue(key);
        model.ChooseKeyCommand.Execute(null);
        await Task.Delay(50);
        Assert.True(model.HasKey);
        Assert.Contains("PRIVATE KEY", _secrets.Get(ShellReach.KeySecret));

        model.ForgetKeyCommand.Execute(null);
        Assert.False(model.HasKey);
    }

    [Fact]
    public void Familiar_sends_a_kit_to_the_kits_folder_on_the_server()
    {
        var host = new FakeHost(Path.Combine(_root, "familiar-host"));
        host.SaveSettings(new FamiliarSettings { Root = Path.Combine(_root, "kits") });
        using (var without = new FamiliarViewModel(host))
            Assert.False(without.CanReachServer);

        _settings.Kind = ReachKinds.Folder;
        _settings.Folder = Path.Combine(_root, "server");
        host.Reach = Reach();
        using var model = new FamiliarViewModel(host);
        Assert.True(model.CanReachServer);
        Assert.Contains(_settings.Folder, model.ServerText);
        Assert.Equal(FamiliarViewModel.DefaultServerKits, model.ServerKits);

        model.ServerKits = "/Data/modules/meows-kit/kits/";
        Assert.Equal("Data/modules/meows-kit/kits", model.ServerKits);

        model.SendToServer(Kit("Goblin Night"));
        Assert.Contains(host.Work.Requested, t => t.Contains("Goblin Night"));
    }

    // ---- against a real SFTP server, when one is named ----

    private static string? Env(string name) => Environment.GetEnvironmentVariable(name) is { Length: > 0 } value ? value : null;

    private bool UseSftpFromEnvironment()
    {
        if (Env("MEOWS_SFTP_HOST") is not { } host || Env("MEOWS_SFTP_KEY") is not { } key || Env("MEOWS_SFTP_ROOT") is not { } root)
            return false;
        _settings.Kind = ReachKinds.Sftp;
        _settings.Host = host;
        _settings.Port = int.TryParse(Env("MEOWS_SFTP_PORT"), out var port) ? port : 22;
        _settings.User = Env("MEOWS_SFTP_USER") ?? Environment.UserName;
        _settings.RemoteRoot = root;
        _secrets.Set(ShellReach.KeySecret, File.ReadAllText(key));
        if (Env("MEOWS_SFTP_PASSPHRASE") is { } passphrase)
            _secrets.Set(ShellReach.PassphraseSecret, passphrase);
        return true;
    }

    [Fact]
    public async Task Over_sftp_nothing_is_copied_until_the_servers_key_is_trusted_and_then_the_kit_lands()
    {
        if (!UseSftpFromEnvironment())
            return;
        var reach = Reach();

        Assert.False(reach.IsSet);
        var first = await reach.Test();
        Assert.True(first.Ok, first.Message);
        Assert.True(first.FingerprintIsNew);
        Assert.NotNull(first.Fingerprint);

        _settings.HostKey = first.Fingerprint;
        var again = await reach.Test();
        Assert.True(again.Ok, again.Message);
        Assert.False(again.FingerprintIsNew);

        var remote = "meows-test-" + Guid.NewGuid().ToString("N")[..8] + "/kits/tavern";
        var sent = await reach.CopyFolder(Kit(), remote);
        Assert.True(sent.Ok, sent.Error);
        Assert.Equal(3, sent.Files);

        // Twice is fine: what is there is replaced, in one step, never half written.
        var resent = await reach.CopyFolder(Kit(), remote);
        Assert.True(resent.Ok, resent.Error);
    }

    [Fact]
    public async Task Over_sftp_a_server_answering_with_another_key_is_refused()
    {
        if (!UseSftpFromEnvironment())
            return;
        _settings.HostKey = "AAAAnotthekeythisserverhasAAAAAAAAAAAAAAAAAA";
        var reach = Reach();

        var sent = await reach.CopyFolder(Kit(), "meows-test-refused/kits/x");
        Assert.False(sent.Ok);
        Assert.Contains("SHA256:AAAAnotthekey", sent.Error);

        var test = await reach.Test();
        Assert.False(test.Ok);
        Assert.True(test.FingerprintIsNew);
    }

    /// <summary>Reports straight away, on the reporting thread, so a test can read the list the moment the copy returns.</summary>
    private sealed class SyncProgress<T>(Action<T> report) : IProgress<T>
    {
        public void Report(T value) => report(value);
    }
}
