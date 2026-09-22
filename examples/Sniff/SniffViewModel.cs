using Avalonia.Threading;
using Meows.Plugins.Abstractions;

namespace Sniff;

/// <summary>
/// Sniffs a folder: how many files, how many bytes, the biggest one. Long enough on a real
/// folder to want the Tasks panel, a progress bar and a Cancel, which is what it is for. The
/// result can be written out through the shell's save dialog.
///
/// The shape to copy: one task at a time, held in a field so a second press cancels the first;
/// the walk reports where it has got to and how far along it is; the token goes into every
/// loop; cancellation is let through, not swallowed; and nothing bound is touched off the UI
/// thread.
/// </summary>
public sealed class SniffViewModel : ObservableObject, IDisposable
{
    private readonly IMeowsHost _host;
    private readonly LanguageWatch _language;
    private IBackgroundTask? _sniff;
    private string? _folder;
    private string? _status;
    private string? _result;
    private bool _isSniffing;

    public SniffViewModel(IMeowsHost host)
    {
        _host = host;
        _language = new LanguageWatch(OnEverythingChanged);
        PickFolderCommand = new RelayCommand(() => _ = PickFolderAsync(), () => !IsSniffing);
        SniffCommand = new RelayCommand(Sniff, () => _folder is not null && !IsSniffing);
        CancelCommand = new RelayCommand(() => _sniff?.Cancel(), () => IsSniffing);
        SaveCommand = new RelayCommand(() => _ = SaveAsync(), () => _result is not null && !IsSniffing);
    }

    public RelayCommand PickFolderCommand { get; }

    public RelayCommand SniffCommand { get; }

    public RelayCommand CancelCommand { get; }

    public RelayCommand SaveCommand { get; }

    public string FolderText => _folder ?? _host.Text["sniff.folder.none"];

    public string Status
    {
        get => _status ?? _host.Text["sniff.status.start"];
        private set => SetField(ref _status, value);
    }

    public string ResultText => _result ?? "";

    public bool HasResult => _result is not null;

    public bool IsSniffing
    {
        get => _isSniffing;
        private set
        {
            if (!SetField(ref _isSniffing, value))
                return;
            PickFolderCommand.RaiseCanExecuteChanged();
            SniffCommand.RaiseCanExecuteChanged();
            CancelCommand.RaiseCanExecuteChanged();
            SaveCommand.RaiseCanExecuteChanged();
        }
    }

    private async Task PickFolderAsync()
    {
        var picked = await _host.Pick.Folder(new PickOptions { Title = _host.Text["sniff.pick"] });
        if (picked is null)
            return;
        _folder = picked;
        _result = null;
        OnPropertyChanged(nameof(FolderText));
        OnPropertyChanged(nameof(ResultText));
        OnPropertyChanged(nameof(HasResult));
        SniffCommand.RaiseCanExecuteChanged();
        SaveCommand.RaiseCanExecuteChanged();
    }

    private void Sniff()
    {
        if (_folder is not { } folder)
            return;

        // The old handle is disposed first, which cancels it: one sniff at a time.
        _sniff?.Dispose();
        IsSniffing = true;
        Status = _host.Text["sniff.status.going"];

        // Shown in the Tasks panel under this plugin's name, with the status and the bar. The
        // shell cancels it if the plugin is switched off or Meows quits; a throw becomes an
        // error notification, not a crash.
        _sniff = _host.Background.Run(_host.Text.Format("sniff.task", Path.GetFileName(folder)), async context =>
        {
            try
            {
                var tally = await Task.Run(() => Walk(folder, context), context.Token);
                await Dispatcher.UIThread.InvokeAsync(() =>
                {
                    _result = _host.Text.Format("sniff.result", tally.Files, Humanise(tally.Bytes),
                        tally.Biggest is null ? "-" : Path.GetFileName(tally.Biggest), Humanise(tally.BiggestBytes));
                    Status = _host.Text["sniff.status.done"];
                    OnPropertyChanged(nameof(ResultText));
                    OnPropertyChanged(nameof(HasResult));
                });
                _host.Log($"Sniff: {folder} holds {tally.Files} files, {tally.Bytes} bytes.");
            }
            catch (OperationCanceledException)
            {
                // Let it through: the shell treats it as a clean stop. Only the tab's own
                // words are set here, on the UI thread, before it goes.
                await Dispatcher.UIThread.InvokeAsync(() => Status = _host.Text["sniff.status.cancelled"]);
                throw;
            }
            finally
            {
                await Dispatcher.UIThread.InvokeAsync(() => IsSniffing = false);
            }
        });
    }

    private sealed record Tally(long Files, long Bytes, string? Biggest, long BiggestBytes);

    /// <summary>
    /// On a thread pool thread. The top level is listed first so the bar has something to
    /// count against; below that every file is one step and the token is checked in the loop.
    /// </summary>
    private Tally Walk(string folder, IBackgroundContext context)
    {
        var top = Directory.EnumerateFileSystemEntries(folder).ToList();
        long files = 0, bytes = 0, biggestBytes = 0;
        string? biggest = null;

        for (var i = 0; i < top.Count; i++)
        {
            context.Token.ThrowIfCancellationRequested();
            context.ReportProgress((double)i / Math.Max(top.Count, 1));
            context.Report(_host.Text.Format("sniff.progress", Path.GetFileName(top[i]), files));

            var entries = Directory.Exists(top[i])
                ? Directory.EnumerateFiles(top[i], "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true })
                : [top[i]];
            foreach (var file in entries)
            {
                context.Token.ThrowIfCancellationRequested();
                long size;
                try
                {
                    size = new FileInfo(file).Length;
                }
                catch (IOException)
                {
                    continue; // Gone or locked between listing and reading; not worth stopping for.
                }

                files++;
                bytes += size;
                if (size > biggestBytes)
                    (biggest, biggestBytes) = (file, size);
            }
        }

        context.ReportProgress(1);
        return new Tally(files, bytes, biggest, biggestBytes);
    }

    private async Task SaveAsync()
    {
        if (_result is not { } result || _folder is not { } folder)
            return;

        var target = await _host.Pick.Save(new PickOptions
        {
            Title = _host.Text["sniff.save"],
            SuggestedName = Path.GetFileName(folder) + ".txt",
            DefaultExtension = "txt",
            Filters = [PickFilter.Of(_host.Text["sniff.save.filter"], "*.txt")],
        });
        if (target is null)
            return;

        await File.WriteAllTextAsync(target, $"{folder}\n{result}\n");
        _host.Log($"Sniff: wrote {target}.");
        Status = _host.Text.Format("sniff.status.saved", Path.GetFileName(target));
    }

    private static string Humanise(long bytes) => bytes switch
    {
        >= 1L << 30 => $"{bytes / (double)(1L << 30):0.0} GB",
        >= 1L << 20 => $"{bytes / (double)(1L << 20):0.0} MB",
        >= 1L << 10 => $"{bytes / (double)(1L << 10):0.0} KB",
        _ => $"{bytes} B",
    };

    public void Dispose()
    {
        _sniff?.Dispose();
        _language.Dispose();
    }
}
