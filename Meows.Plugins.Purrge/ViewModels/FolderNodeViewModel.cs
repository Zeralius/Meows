using System.Collections.ObjectModel;
using Meows.Plugins.Abstractions;

namespace Meows.Plugins.Purrge.ViewModels;

/// <summary>
/// A node in the folder tree. Children load when you expand it, because walking a whole drive
/// up front would freeze the UI to fill in branches nobody opened.
/// </summary>
public sealed class FolderNodeViewModel : ObservableObject
{
    private bool _isExpanded;
    private bool _loaded;

    public FolderNodeViewModel(string path, string? displayName = null)
    {
        Path = path;
        Name = displayName ?? SafeName(path);
        // Gives the node an arrow before we know what is under it.
        Children = [Placeholder];
    }

    private static FolderNodeViewModel Placeholder { get; } = new("", "Loading…");

    public string Path { get; }

    public string Name { get; }

    public ObservableCollection<FolderNodeViewModel> Children { get; }

    public bool IsExpanded
    {
        get => _isExpanded;
        set
        {
            if (!SetField(ref _isExpanded, value) || !value)
                return;
            LoadChildren();
        }
    }

    public static ObservableCollection<FolderNodeViewModel> CreateRoots()
    {
        var roots = new ObservableCollection<FolderNodeViewModel>();
        foreach (var drive in SafeDrives())
            roots.Add(new FolderNodeViewModel(drive.RootDirectory.FullName, DescribeDrive(drive)));
        return roots;
    }

    private void LoadChildren()
    {
        if (_loaded)
            return;
        _loaded = true;

        Children.Clear();
        foreach (var directory in SafeSubdirectories(Path))
            Children.Add(new FolderNodeViewModel(directory));
    }

    private static IEnumerable<DriveInfo> SafeDrives()
    {
        DriveInfo[] drives;
        try
        {
            drives = DriveInfo.GetDrives();
        }
        catch (Exception)
        {
            return [];
        }

        return drives.Where(d =>
        {
            try
            {
                return d.IsReady;
            }
            catch (Exception)
            {
                return false;
            }
        });
    }

    private static string DescribeDrive(DriveInfo drive)
    {
        try
        {
            var label = drive.VolumeLabel;
            return string.IsNullOrWhiteSpace(label)
                ? drive.Name
                : $"{drive.Name.TrimEnd('\\')}  {label}";
        }
        catch (Exception)
        {
            return drive.Name;
        }
    }

    private static IEnumerable<string> SafeSubdirectories(string path)
    {
        try
        {
            return Directory.GetDirectories(path)
                .Where(d =>
                {
                    try
                    {
                        var info = new DirectoryInfo(d);
                        // Just noise when you are picking somewhere to scan.
                        return !info.Attributes.HasFlag(FileAttributes.Hidden) &&
                               !info.Attributes.HasFlag(FileAttributes.System);
                    }
                    catch (Exception)
                    {
                        return false;
                    }
                })
                .OrderBy(System.IO.Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch (Exception)
        {
            return [];
        }
    }

    private static string SafeName(string path)
    {
        var name = System.IO.Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }
}
