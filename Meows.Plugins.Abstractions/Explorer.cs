using System.Diagnostics;

namespace Meows.Plugins.Abstractions;

/// <summary>
/// The three ways a plugin hands something to Windows: open it with whatever opens it, show it
/// in Explorer with the file selected, or ask which program to open it with.
///
/// Every plugin had written the first of these, and four had written the second with the
/// <c>/select,</c> incantation, comma and all. They live here so the incantation is typed once.
/// Each throws on failure the way <see cref="Process.Start(ProcessStartInfo)"/> does, so a
/// caller's existing try/catch and error line keep working unchanged.
/// </summary>
public static class Explorer
{
    /// <summary>A file in its default program, a folder in Explorer, a URL in the browser.</summary>
    public static void Open(string target) =>
        Process.Start(new ProcessStartInfo { FileName = target, UseShellExecute = true });

    /// <summary>Explorer with the file selected, rather than merely its folder open.</summary>
    public static void Reveal(string path) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = "explorer.exe",
            ArgumentList = { "/select,", path },
            UseShellExecute = true,
        });

    /// <summary>
    /// Windows' own "Open with" dialog. The shell exposes it through rundll32, which is how
    /// Explorer's own menu entry reaches it too.
    /// </summary>
    public static void OpenWith(string path) =>
        Process.Start(new ProcessStartInfo
        {
            FileName = "rundll32.exe",
            ArgumentList = { "shell32.dll,OpenAs_RunDLL", path },
            UseShellExecute = false,
        });
}
