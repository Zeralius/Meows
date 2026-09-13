using System.Text;
using Meows.Services;

namespace Meows.Tests;

/// <summary>The shell's credential store. Windows only, like the app.</summary>
public class SecretStoreTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-secrets-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    [Fact]
    public void What_is_saved_comes_back_and_is_not_on_disk_in_clear()
    {
        var secrets = new SecretStore(_root);

        secrets.Set("bluesky", "{\"password\":\"xxxx-yyyy-zzzz\"}");

        Assert.True(secrets.Has("bluesky"));
        Assert.Equal("{\"password\":\"xxxx-yyyy-zzzz\"}", secrets.Get("bluesky"));

        // Under secrets\ in the plugin folder, which is where Scruff kept its own before the
        // shell took over, so nothing already saved had to move.
        var raw = File.ReadAllBytes(Path.Combine(_root, "secrets", "bluesky.secret"));
        Assert.DoesNotContain("xxxx-yyyy-zzzz", Encoding.UTF8.GetString(raw));
        Assert.DoesNotContain("xxxx-yyyy-zzzz", Encoding.Unicode.GetString(raw));

        secrets.Forget("bluesky");
        Assert.False(secrets.Has("bluesky"));
        Assert.Null(secrets.Get("bluesky"));
    }

    [Fact]
    public void A_file_that_cannot_be_opened_reads_as_nothing_rather_than_throwing()
    {
        Directory.CreateDirectory(Path.Combine(_root, "secrets"));
        File.WriteAllBytes(Path.Combine(_root, "secrets", "mastodon.secret"), [1, 2, 3, 4]);

        Assert.Null(new SecretStore(_root).Get("mastodon"));
    }

    [Fact]
    public void A_name_that_is_not_a_file_name_is_still_a_name()
    {
        var secrets = new SecretStore(_root);

        secrets.Set("what/ever:this", "v");

        Assert.Equal("v", secrets.Get("what/ever:this"));
    }
}
