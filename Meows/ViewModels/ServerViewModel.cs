using Meows.Plugins.Abstractions;
using Meows.Services;

namespace Meows.ViewModels;

/// <summary>
/// The Settings tab's Server section: where plugins can copy to. Nothing, a folder, or SFTP with
/// a key. Every change is saved as it is made, like the rest of the tab; the key and its
/// passphrase go to the sealed secrets and never into the preferences file.
///
/// SFTP is not usable until a person has seen the server's key and pressed Trust. A test shows
/// it; after that a server answering with another key is refused until trusted again.
/// </summary>
public sealed class ServerViewModel : ObservableObject
{
    private readonly ReachSettings _reach;
    private readonly ShellReach _service;
    private readonly IMeowsSecrets _secrets;
    private readonly IMeowsPicker _picker;
    private readonly Action _save;
    private readonly Action<string> _log;
    private string? _testResult;
    private bool _testOk;
    private bool _isTesting;
    private string? _offeredKey;
    private string _passphrase = "";

    public ServerViewModel(ReachSettings reach, ShellReach service, IMeowsSecrets secrets, IMeowsPicker picker, Action save, Action<string> log)
    {
        _reach = reach;
        _service = service;
        _secrets = secrets;
        _picker = picker;
        _save = save;
        _log = log;

        BrowseFolderCommand = new RelayCommand(() => _ = BrowseFolderAsync());
        ChooseKeyCommand = new RelayCommand(() => _ = ChooseKeyAsync());
        ForgetKeyCommand = new RelayCommand(ForgetKey, () => HasKey);
        TestCommand = new RelayCommand(() => _ = TestAsync(), () => !IsTesting && !IsNone);
        TrustCommand = new RelayCommand(Trust, () => _offeredKey is not null);
    }

    public RelayCommand BrowseFolderCommand { get; }

    public RelayCommand ChooseKeyCommand { get; }

    public RelayCommand ForgetKeyCommand { get; }

    public RelayCommand TestCommand { get; }

    public RelayCommand TrustCommand { get; }

    // ---- which kind ----

    public bool IsNone
    {
        get => _reach.Kind == ReachKinds.None;
        set { if (value) SetKind(ReachKinds.None); }
    }

    public bool IsFolder
    {
        get => _reach.Kind == ReachKinds.Folder;
        set { if (value) SetKind(ReachKinds.Folder); }
    }

    public bool IsSftp
    {
        get => _reach.Kind == ReachKinds.Sftp;
        set { if (value) SetKind(ReachKinds.Sftp); }
    }

    private void SetKind(string kind)
    {
        if (_reach.Kind == kind)
            return;
        _reach.Kind = kind;
        Saved($"The server is now reached as '{kind}'.");
        ClearTest();
        OnPropertyChanged(nameof(IsNone));
        OnPropertyChanged(nameof(IsFolder));
        OnPropertyChanged(nameof(IsSftp));
        TestCommand.RaiseCanExecuteChanged();
    }

    // ---- a folder ----

    public string Folder
    {
        get => _reach.Folder ?? "";
        set
        {
            var trimmed = (value ?? "").Trim();
            if ((_reach.Folder ?? "") == trimmed)
                return;
            _reach.Folder = trimmed.Length == 0 ? null : trimmed;
            Saved(null);
            ClearTest();
            OnPropertyChanged();
        }
    }

    private async Task BrowseFolderAsync()
    {
        var picked = await _picker.Folder(new PickOptions { Title = MeowsText.Current["settings.server.folder.pick"] });
        if (picked is not null)
            Folder = picked;
    }

    // ---- SFTP ----

    public string Host
    {
        get => _reach.Host ?? "";
        set => SetAddress(value, _reach.Host, v => _reach.Host = v);
    }

    public string User
    {
        get => _reach.User ?? "";
        set => SetAddress(value, _reach.User, v => _reach.User = v);
    }

    public string RemoteRoot
    {
        get => _reach.RemoteRoot ?? "";
        set
        {
            var trimmed = (value ?? "").Trim();
            if ((_reach.RemoteRoot ?? "") == trimmed)
                return;
            _reach.RemoteRoot = trimmed.Length == 0 ? null : trimmed;
            Saved(null);
            ClearTest();
            OnPropertyChanged();
        }
    }

    public int Port
    {
        get => _reach.Port;
        set
        {
            if (value <= 0 || value > 65535 || _reach.Port == value)
                return;
            _reach.Port = value;
            // Another port can be another server; its key has to be looked at again.
            Untrust();
            Saved(null);
            ClearTest();
            OnPropertyChanged();
        }
    }

    /// <summary>A different host or account is a different server, and the key trusted for the old one does not carry over.</summary>
    private void SetAddress(string? value, string? current, Action<string?> set, [System.Runtime.CompilerServices.CallerMemberName] string? name = null)
    {
        var trimmed = (value ?? "").Trim();
        if ((current ?? "") == trimmed)
            return;
        set(trimmed.Length == 0 ? null : trimmed);
        if (name == nameof(Host))
            Untrust();
        Saved(null);
        ClearTest();
        OnPropertyChanged(name);
    }

    private void Untrust()
    {
        if (_reach.HostKey is null)
            return;
        _reach.HostKey = null;
        _log("Forgot the server's trusted key: the address changed.");
        OnPropertyChanged(nameof(TrustedKeyText));
        OnPropertyChanged(nameof(HasTrustedKey));
    }

    public bool HasKey => _secrets.Has(ShellReach.KeySecret);

    public string KeyText => MeowsText.Current[HasKey ? "settings.server.key.kept" : "settings.server.key.none"];

    private async Task ChooseKeyAsync()
    {
        var picked = await _picker.File(new PickOptions
        {
            Title = MeowsText.Current["settings.server.key.pick"],
            StartIn = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".ssh"),
        });
        if (picked is null)
            return;

        try
        {
            var text = File.ReadAllText(picked);
            if (!text.Contains("PRIVATE KEY", StringComparison.Ordinal))
            {
                TestResult = MeowsText.Current.Format("settings.server.key.notakey", Path.GetFileName(picked));
                TestOk = false;
                return;
            }
            _secrets.Set(ShellReach.KeySecret, text);
            _log($"Kept an SSH key from {picked}, sealed to this account. The file itself was not touched.");
        }
        catch (Exception ex)
        {
            TestResult = MeowsText.Current.Format("settings.server.key.unreadable", ex.Message);
            TestOk = false;
            return;
        }

        ClearTest();
        RaiseKey();
    }

    private void ForgetKey()
    {
        _secrets.Forget(ShellReach.KeySecret);
        _secrets.Forget(ShellReach.PassphraseSecret);
        _log("Forgot the SSH key and its passphrase.");
        Passphrase = "";
        ClearTest();
        RaiseKey();
    }

    /// <summary>
    /// Typed here, kept sealed, never shown again: the box is empty after a restart even when a
    /// passphrase is kept, and <see cref="PassphraseHint"/> says so.
    /// </summary>
    public string Passphrase
    {
        get => _passphrase;
        set
        {
            value ??= "";
            if (_passphrase == value)
                return;
            _passphrase = value;
            if (value.Length == 0)
                _secrets.Forget(ShellReach.PassphraseSecret);
            else
                _secrets.Set(ShellReach.PassphraseSecret, value);
            OnPropertyChanged();
            OnPropertyChanged(nameof(PassphraseHint));
        }
    }

    public string PassphraseHint => MeowsText.Current[
        _passphrase.Length == 0 && _secrets.Has(ShellReach.PassphraseSecret) ? "settings.server.passphrase.kept" : "settings.server.passphrase"];

    private void RaiseKey()
    {
        OnPropertyChanged(nameof(HasKey));
        OnPropertyChanged(nameof(KeyText));
        OnPropertyChanged(nameof(PassphraseHint));
        ForgetKeyCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(StateText));
    }

    public bool HasTrustedKey => !string.IsNullOrWhiteSpace(_reach.HostKey);

    public string TrustedKeyText => HasTrustedKey
        ? MeowsText.Current.Format("settings.server.trusted", _reach.HostKey!)
        : MeowsText.Current["settings.server.untrusted"];

    // ---- testing and trusting ----

    public bool IsTesting
    {
        get => _isTesting;
        private set
        {
            if (SetField(ref _isTesting, value))
                TestCommand.RaiseCanExecuteChanged();
        }
    }

    public string? TestResult
    {
        get => _testResult;
        private set
        {
            if (SetField(ref _testResult, value))
                OnPropertyChanged(nameof(HasTestResult));
        }
    }

    public bool HasTestResult => !string.IsNullOrEmpty(_testResult);

    public bool TestOk
    {
        get => _testOk;
        private set => SetField(ref _testOk, value);
    }

    /// <summary>A key the last test saw that is not the trusted one, waiting for a person to look at it.</summary>
    public bool HasOfferedKey => _offeredKey is not null;

    public async Task TestAsync()
    {
        IsTesting = true;
        TestResult = MeowsText.Current["settings.server.testing"];
        TestOk = false;
        try
        {
            var test = await _service.Test();
            TestResult = test.Message;
            TestOk = test.Ok;
            _offeredKey = test.FingerprintIsNew ? test.Fingerprint : null;
        }
        catch (Exception ex)
        {
            TestResult = ex.Message;
            _offeredKey = null;
        }
        finally
        {
            IsTesting = false;
            OnPropertyChanged(nameof(HasOfferedKey));
            TrustCommand.RaiseCanExecuteChanged();
            OnPropertyChanged(nameof(StateText));
        }
    }

    /// <summary>The key the test showed becomes the one this server has to answer with.</summary>
    public void Trust()
    {
        if (_offeredKey is not { } key)
            return;
        _reach.HostKey = key;
        _offeredKey = null;
        Saved($"Trusted the server's key SHA256:{key}.");
        TestResult = MeowsText.Current["settings.server.trusted.now"];
        TestOk = true;
        OnPropertyChanged(nameof(HasOfferedKey));
        OnPropertyChanged(nameof(HasTrustedKey));
        OnPropertyChanged(nameof(TrustedKeyText));
        TrustCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(StateText));
    }

    private void ClearTest()
    {
        TestResult = null;
        _offeredKey = null;
        OnPropertyChanged(nameof(HasOfferedKey));
        TrustCommand.RaiseCanExecuteChanged();
        OnPropertyChanged(nameof(StateText));
    }

    /// <summary>One line under the heading: where plugins' copies go, or what is still missing.</summary>
    public string StateText => _service.Missing() is { } missing
        ? missing
        : MeowsText.Current.Format("settings.server.ready", _service.Where ?? "");

    private void Saved(string? line)
    {
        _save();
        if (line is not null)
            _log(line);
        OnPropertyChanged(nameof(StateText));
    }
}
