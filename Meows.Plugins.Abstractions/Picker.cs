namespace Meows.Plugins.Abstractions;

/// <summary>One entry in a file dialog's type list: "Pictures" and the patterns that count.</summary>
public sealed record PickFilter(string Name, IReadOnlyList<string> Patterns)
{
    public static PickFilter Of(string name, params string[] patterns) => new(name, patterns);
}

/// <summary>What a file dialog is told. Everything is optional; a plain "pick a file" passes nothing.</summary>
public sealed record PickOptions
{
    /// <summary>The dialog's title. Null lets the platform choose.</summary>
    public string? Title { get; init; }

    /// <summary>Types to offer. Null or empty shows every file.</summary>
    public IReadOnlyList<PickFilter>? Filters { get; init; }

    /// <summary>Where to start. Null is wherever the platform last was.</summary>
    public string? StartIn { get; init; }

    /// <summary>For a save dialog: the name already in the box.</summary>
    public string? SuggestedName { get; init; }

    /// <summary>For a save dialog: added when the user types a name without one.</summary>
    public string? DefaultExtension { get; init; }
}

/// <summary>
/// The shell's file dialogs, handed to a plugin so its view model can ask for a file without
/// reaching into the visual tree for a TopLevel. Every call answers null, or an empty list,
/// when the user cancelled and also when there is no window to show a dialog over, which is
/// what a plugin driven from the tray or a test gets. Since 1.0.0.
/// </summary>
public interface IMeowsPicker
{
    /// <summary>One existing file, as a full path.</summary>
    Task<string?> File(PickOptions? options = null);

    /// <summary>Any number of existing files. Empty when cancelled.</summary>
    Task<IReadOnlyList<string>> Files(PickOptions? options = null);

    /// <summary>One existing folder.</summary>
    Task<string?> Folder(PickOptions? options = null);

    /// <summary>Any number of existing folders. Empty when cancelled.</summary>
    Task<IReadOnlyList<string>> Folders(PickOptions? options = null);

    /// <summary>Where to write a file. The file may not exist yet, and may exist and be meant to be replaced.</summary>
    Task<string?> Save(PickOptions? options = null);
}

/// <summary>What a host with no window answers: nothing was picked.</summary>
public sealed class NoPicker : IMeowsPicker
{
    public static NoPicker Instance { get; } = new();

    public Task<string?> File(PickOptions? options = null) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> Files(PickOptions? options = null) => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> Folder(PickOptions? options = null) => Task.FromResult<string?>(null);

    public Task<IReadOnlyList<string>> Folders(PickOptions? options = null) => Task.FromResult<IReadOnlyList<string>>([]);

    public Task<string?> Save(PickOptions? options = null) => Task.FromResult<string?>(null);
}
