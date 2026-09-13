namespace Meows.Plugins.Abstractions;

/// <summary>
/// Where a plugin keeps a credential: an app password, an access token, anything that must not
/// sit in settings.json in clear text.
///
/// The shell owns the sealing. Each secret is a file of its own under the plugin's data folder,
/// protected for the current Windows user, so it opens on this machine for this account and is
/// noise anywhere else. Names are scoped to the plugin. Prefer something revocable: an app
/// password over an account password, a token over a login.
/// </summary>
public interface IMeowsSecrets
{
    bool Has(string name);

    /// <summary>Null when there is none, and also when it cannot be opened, which is another user's file.</summary>
    string? Get(string name);

    void Set(string name, string value);

    void Forget(string name);
}
