using Microsoft.Win32;

namespace Meows.Services;

/// <summary>
/// How much bigger than designed the whole window is drawn. Avalonia does not read the Text size
/// slider in Windows' accessibility settings, so somebody who turned it up got Meows at 11 pixels
/// anyway. Unless a size has been picked on the Settings tab, this follows that slider.
/// </summary>
public static class TextScale
{
    /// <summary>The sizes offered on the Settings tab, in percent. Null there means follow Windows.</summary>
    public static IReadOnlyList<int> Choices { get; } = [100, 115, 130, 150, 175];

    public const int Smallest = 100;
    public const int Largest = 200;

    /// <summary>The percentage to draw at: the one picked, or Windows' text size, or 100.</summary>
    public static int Resolve(int? picked) => Math.Clamp(picked ?? Windows() ?? 100, Smallest, Largest);

    /// <summary>Windows' own text size, 100 to 225, or null where there is none to read.</summary>
    public static int? Windows()
    {
        if (!OperatingSystem.IsWindows())
            return null;
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Accessibility");
            return key?.GetValue("TextScaleFactor") is int factor and >= 100 and <= 225 ? factor : null;
        }
        catch (Exception)
        {
            return null;
        }
    }
}
