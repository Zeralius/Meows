using Meows.Plugins.Abstractions;
using Meows.Plugins.TelegramPoster.Services;
using Meows.Bot;

namespace Meows.Plugins.TelegramPoster.ViewModels;

/// <summary>
/// The first-run panel. Clone the bot, install its dependencies, give it a token. Shows
/// whenever there is no usable checkout, and just the token step when .env has none.
/// </summary>
public sealed class BotSetupViewModel : ObservableObject
{
    private readonly Action<string> _log;
    private readonly Action<string> _onBotRootReady;
    private readonly Func<BotWorkspace?> _currentWorkspace;
    private readonly Func<string> _pythonPath;
    private readonly Action<string> _onRepositoryUrlChanged;

    private string _repositoryUrl = "";
    private string _destination = "";
    private string _tokenInput = "";
    private bool _isBusy;
    private string _statusMessage = "";
    private string? _errorMessage;
    private bool? _dependenciesPresent;
    private bool _pythonAvailable = true;
    private bool _gitAvailable = true;
    private string _toolStatusText = "Checking for Python and git…";

    public BotSetupViewModel(
        Action<string> log,
        Action<string> onBotRootReady,
        Func<BotWorkspace?> currentWorkspace,
        Func<string> pythonPath,
        Action<string> onRepositoryUrlChanged)
    {
        _log = log;
        _onBotRootReady = onBotRootReady;
        _currentWorkspace = currentWorkspace;
        _pythonPath = pythonPath;
        _onRepositoryUrlChanged = onRepositoryUrlChanged;

        _destination = BotSetup.DefaultCloneDestination();

        CloneCommand = new RelayCommand(() => _ = CloneAsync(), () => !IsBusy && _gitAvailable);
        InstallCommand = new RelayCommand(() => _ = InstallAsync(), () => !IsBusy && HasWorkspace && _pythonAvailable);
        CheckDependenciesCommand = new RelayCommand(() => _ = CheckDependenciesAsync(), () => !IsBusy && HasWorkspace && _pythonAvailable);
        SaveTokenCommand = new RelayCommand(SaveToken, () => !IsBusy && HasWorkspace && TokenInput.Trim().Length > 0);
    }

    public RelayCommand CloneCommand { get; }

    public RelayCommand InstallCommand { get; }

    public RelayCommand CheckDependenciesCommand { get; }

    public RelayCommand SaveTokenCommand { get; }

    /// <summary>Prefilled with the public repo, then saved once you change it.</summary>
    public string RepositoryUrl
    {
        get => _repositoryUrl;
        set
        {
            if (SetField(ref _repositoryUrl, value))
                _onRepositoryUrlChanged(value);
        }
    }

    public string Destination
    {
        get => _destination;
        set => SetField(ref _destination, value);
    }

    public string TokenInput
    {
        get => _tokenInput;
        set
        {
            if (SetField(ref _tokenInput, value))
                SaveTokenCommand.RaiseCanExecuteChanged();
        }
    }

    public bool IsBusy
    {
        get => _isBusy;
        private set
        {
            if (!SetField(ref _isBusy, value))
                return;
            CloneCommand.RaiseCanExecuteChanged();
            InstallCommand.RaiseCanExecuteChanged();
            CheckDependenciesCommand.RaiseCanExecuteChanged();
            SaveTokenCommand.RaiseCanExecuteChanged();
        }
    }

    public string StatusMessage
    {
        get => _statusMessage;
        private set => SetField(ref _statusMessage, value);
    }

    public string? ErrorMessage
    {
        get => _errorMessage;
        private set
        {
            if (SetField(ref _errorMessage, value))
                OnPropertyChanged(nameof(HasError));
        }
    }

    public bool HasError => !string.IsNullOrEmpty(ErrorMessage);

    public bool HasWorkspace => _currentWorkspace()?.LooksValid == true;

    public bool HasToken => _currentWorkspace()?.HasToken == true;

    /// <summary>Null until we have actually looked, so the UI can say so.</summary>
    public bool? DependenciesPresent
    {
        get => _dependenciesPresent;
        private set
        {
            if (SetField(ref _dependenciesPresent, value))
                OnPropertyChanged(nameof(DependencyStatusText));
        }
    }

    public string DependencyStatusText => DependenciesPresent switch
    {
        true => "aiogram, apscheduler and python-dotenv are importable",
        false => "Dependencies are missing, so the bot cannot start",
        _ => "Not checked yet",
    };

    /// <summary>Pushed in by the parent, which probes once for the whole tab.</summary>
    public void SetToolStatus(bool pythonAvailable, bool gitAvailable, string statusText)
    {
        _pythonAvailable = pythonAvailable;
        _gitAvailable = gitAvailable;
        _toolStatusText = statusText;
        OnPropertyChanged(nameof(PythonAvailable));
        OnPropertyChanged(nameof(GitAvailable));
        OnPropertyChanged(nameof(ToolStatusText));
        CloneCommand.RaiseCanExecuteChanged();
        InstallCommand.RaiseCanExecuteChanged();
        CheckDependenciesCommand.RaiseCanExecuteChanged();
    }

    public bool PythonAvailable => _pythonAvailable;

    public bool GitAvailable => _gitAvailable;

    public string ToolStatusText => _toolStatusText;

    public string CloneStepStatus => HasWorkspace ? "done" : "todo";

    public string TokenStepStatus => HasToken ? "done" : "todo";

    public void Refresh()
    {
        OnPropertyChanged(nameof(HasWorkspace));
        OnPropertyChanged(nameof(HasToken));
        OnPropertyChanged(nameof(CloneStepStatus));
        OnPropertyChanged(nameof(TokenStepStatus));
        InstallCommand.RaiseCanExecuteChanged();
        CheckDependenciesCommand.RaiseCanExecuteChanged();
        SaveTokenCommand.RaiseCanExecuteChanged();
    }

    private async Task CloneAsync()
    {
        ErrorMessage = null;

        var url = RepositoryUrl.Trim();
        if (url.Length == 0)
        {
            ErrorMessage = "Enter the repository URL to clone from.";
            return;
        }

        var problem = BotSetup.DestinationProblem(Destination);
        if (problem is not null)
        {
            ErrorMessage = problem;
            return;
        }

        IsBusy = true;
        StatusMessage = "Cloning…";
        try
        {
            var result = await BotSetup.CloneAsync(url, Destination, _log);

            if (!result.Started)
            {
                ErrorMessage = $"Could not run git: {result.FailureReason}. Is git installed and on PATH?";
                StatusMessage = "";
                return;
            }

            if (!result.Succeeded)
            {
                // Usually credentials. We suppress the prompt on purpose, so say so.
                ErrorMessage = $"git clone failed (exit {result.ExitCode}). See the log for details. " +
                               "If the repository is private, make sure your credentials are already set up for it.";
                StatusMessage = "";
                return;
            }

            StatusMessage = "Cloned.";
            _onBotRootReady(Path.GetFullPath(Destination));
            Refresh();
            await CheckDependenciesAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task InstallAsync()
    {
        var workspace = _currentWorkspace();
        if (workspace is null)
            return;

        ErrorMessage = null;
        IsBusy = true;
        StatusMessage = "Installing dependencies…";
        try
        {
            var result = await BotSetup.InstallDependenciesAsync(_pythonPath(), workspace.Root, _log);

            if (!result.Started)
                ErrorMessage = $"Could not run python: {result.FailureReason}. Is Python installed and on PATH?";
            else if (!result.Succeeded)
                ErrorMessage = $"pip install failed (exit {result.ExitCode}). See the log.";

            StatusMessage = result.Succeeded ? "Dependencies installed." : "";
            await CheckDependenciesAsync();
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task CheckDependenciesAsync()
    {
        var workspace = _currentWorkspace();
        if (workspace is null)
            return;

        DependenciesPresent = await BotSetup.DependenciesPresentAsync(_pythonPath(), workspace.Root);
    }

    private void SaveToken()
    {
        var workspace = _currentWorkspace();
        if (workspace is null)
            return;

        ErrorMessage = null;
        try
        {
            BotSetup.WriteToken(workspace.Root, TokenInput.Trim());
            // Clear it straight away. No reason to keep it around.
            TokenInput = "";
            StatusMessage = "Token written to .env";
            _log("Wrote BOT_TOKEN to .env");
            Refresh();
        }
        catch (Exception ex)
        {
            ErrorMessage = $"Could not write .env: {ex.Message}";
        }
    }
}
