using System.Collections.ObjectModel;
using Meows.Disk;
using Meows.Plugins.Abstractions;

namespace Meows.Plugins.Chonk.ViewModels;

/// <summary>
/// A drive, or a folder somewhere under one, in the tree on the left.
///
/// Nothing is read until it has to be. A drive starts with one stand-in child so it draws with
/// an arrow, and the first time it is opened the stand-in is replaced by what is really there.
/// Listing a folder's immediate children is cheap; listing a drive's whole tree is the scan, and
/// the scan only ever runs when it is asked for.
/// </summary>
public sealed class NodeViewModel : ObservableObject
{
    /// <summary>Stands in for children not yet listed, so the arrow is drawn.</summary>
    private static readonly NodeViewModel Placeholder = new("", "", isDrive: false);

    private bool _isExpanded;
    private bool _isLoaded;

    private NodeViewModel(string path, string name, bool isDrive)
    {
        Path = path;
        Name = name;
        IsDrive = isDrive;
    }

    public static NodeViewModel ForDrive(DriveInfo drive)
    {
        var node = new NodeViewModel(drive.RootDirectory.FullName,
            string.IsNullOrWhiteSpace(drive.VolumeLabel) ? drive.Name : $"{drive.Name} {drive.VolumeLabel}",
            isDrive: true)
        {
            UsageText = MeowsText.Current.Format("chonk.drive.usage",
                DiskScan.Humanise(drive.TotalSize - drive.TotalFreeSpace),
                DiskScan.Humanise(drive.TotalSize)),
            Fraction = drive.TotalSize <= 0
                ? 0
                : (double)(drive.TotalSize - drive.TotalFreeSpace) / drive.TotalSize,
        };

        node.Children.Add(Placeholder);
        return node;
    }

    public static NodeViewModel ForFolder(string path)
    {
        var node = new NodeViewModel(path, System.IO.Path.GetFileName(path.TrimEnd('\\', '/')), isDrive: false);
        node.Children.Add(Placeholder);
        return node;
    }

    public string Path { get; }

    public string Name { get; }

    public bool IsDrive { get; }

    public bool IsPlaceholder => Path.Length == 0;

    public string UsageText { get; private init; } = "";

    public double Fraction { get; private init; }

    public ObservableCollection<NodeViewModel> Children { get; } = [];

    /// <summary>Opening a node is what lists it. Closing it again keeps what was listed.</summary>
    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetField(ref _isExpanded, value))
                return;

            if (value)
                Load();
        }
    }

    /// <summary>
    /// The folders directly inside this one, listed once.
    ///
    /// System folders are left out: the recycle bin and System Volume Information are not places
    /// anyone chooses to measure, and the tree is for choosing. Hidden folders stay, because
    /// AppData is hidden and is exactly the sort of place worth measuring.
    /// </summary>
    public void Load()
    {
        if (_isLoaded)
            return;

        _isLoaded = true;
        Children.Clear();

        IEnumerable<string> folders;
        try
        {
            folders = Directory.EnumerateDirectories(Path)
                .Where(f => !IsSystem(f))
                .OrderBy(f => f, StringComparer.CurrentCultureIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            // Unreadable. An empty node is the honest answer, and the scan would say the same.
            return;
        }

        foreach (var folder in folders)
            Children.Add(ForFolder(folder));
    }

    private static bool IsSystem(string folder)
    {
        try
        {
            return (File.GetAttributes(folder) & FileAttributes.System) != 0;
        }
        catch (Exception)
        {
            return true;
        }
    }

    /// <summary>The child holding this path, listing on the way if it has to.</summary>
    public NodeViewModel? Find(string path)
    {
        Load();

        return Children.FirstOrDefault(c =>
            string.Equals(c.Path.TrimEnd('\\', '/'), path.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
    }
}
