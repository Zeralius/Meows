using Avalonia.Controls;
using Avalonia.Platform.Storage;
using Meows.Plugins.Abstractions;

namespace Meows.Services;

/// <summary>
/// The file dialogs, over whichever window is showing. Asked for its TopLevel each time rather
/// than given one, because the window comes and goes with the tray; with nothing showing there
/// is nothing to put a dialog over, and the answer is that nothing was picked.
/// </summary>
public sealed class ShellPicker(Func<TopLevel?> topLevel) : IMeowsPicker
{
    private IStorageProvider? Storage => topLevel()?.StorageProvider;

    public async Task<string?> File(PickOptions? options = null)
    {
        if (Storage is not { } storage)
            return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = options?.Title,
            AllowMultiple = false,
            FileTypeFilter = Filters(options),
            SuggestedStartLocation = await StartIn(storage, options),
        });
        return files.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<IReadOnlyList<string>> Files(PickOptions? options = null)
    {
        if (Storage is not { } storage)
            return [];

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = options?.Title,
            AllowMultiple = true,
            FileTypeFilter = Filters(options),
            SuggestedStartLocation = await StartIn(storage, options),
        });
        return files.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> Folder(PickOptions? options = null)
    {
        if (Storage is not { } storage)
            return null;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = options?.Title,
            AllowMultiple = false,
            SuggestedStartLocation = await StartIn(storage, options),
        });
        return folders.FirstOrDefault()?.TryGetLocalPath();
    }

    public async Task<IReadOnlyList<string>> Folders(PickOptions? options = null)
    {
        if (Storage is not { } storage)
            return [];

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = options?.Title,
            AllowMultiple = true,
            SuggestedStartLocation = await StartIn(storage, options),
        });
        return folders.Select(f => f.TryGetLocalPath()).OfType<string>().ToList();
    }

    public async Task<string?> Save(PickOptions? options = null)
    {
        if (Storage is not { } storage)
            return null;

        var file = await storage.SaveFilePickerAsync(new FilePickerSaveOptions
        {
            Title = options?.Title,
            SuggestedFileName = options?.SuggestedName,
            DefaultExtension = options?.DefaultExtension,
            FileTypeChoices = Filters(options),
            SuggestedStartLocation = await StartIn(storage, options),
        });
        return file?.TryGetLocalPath();
    }

    private static List<FilePickerFileType>? Filters(PickOptions? options) =>
        options?.Filters is { Count: > 0 } filters
            ? filters.Select(f => new FilePickerFileType(f.Name) { Patterns = f.Patterns.ToList() }).ToList()
            : null;

    private static async Task<IStorageFolder?> StartIn(IStorageProvider storage, PickOptions? options)
    {
        if (options?.StartIn is not { } path || !Directory.Exists(path))
            return null;
        try
        {
            return await storage.TryGetFolderFromPathAsync(path);
        }
        catch (Exception)
        {
            return null;
        }
    }
}
