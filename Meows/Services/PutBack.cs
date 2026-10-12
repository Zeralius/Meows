using Meows.Disk;
using Meows.Plugins.Abstractions;

namespace Meows.Services;

/// <summary>
/// What *Put back* on a History line asks. The plugin first, when its tab is open and it knows
/// the line, which is how Kibble takes a file back out of a queue. Then, for anything any plugin
/// sent to the Recycle Bin and recorded the shared way, the shell itself, from the bin, whether
/// that plugin is open or not: until this only Kibble's lines could be put back, and Litter,
/// Mouser, Molt and Chonk did not even say what they had recycled.
/// </summary>
public sealed class PutBack(IUndoTarget? plugin) : IUndoTarget
{
    public bool CanUndo(StoredEvent stored) =>
        PluginCan(stored) || Recycled.StillInBin(stored).Count > 0;

    public string? Undo(StoredEvent stored) =>
        PluginCan(stored) ? plugin!.Undo(stored) : Recycled.PutBack(stored);

    private bool PluginCan(StoredEvent stored)
    {
        try
        {
            return plugin?.CanUndo(stored) == true;
        }
        catch (Exception)
        {
            return false;
        }
    }
}
