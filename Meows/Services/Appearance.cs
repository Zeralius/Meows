using Avalonia;
using Avalonia.Styling;

namespace Meows.Services;

/// <summary>
/// Turns a saved theme choice into something Avalonia understands, and puts it on the running
/// application.
///
/// <c>ThemeVariant.Default</c> is not a third colour scheme: it means follow whatever Windows is
/// set to, and Avalonia keeps watching after that, so switching the desktop to light mode moves
/// the window with it.
/// </summary>
public static class Appearance
{
    public const string System = "system";
    public const string Light = "light";
    public const string Dark = "dark";

    public static ThemeVariant VariantFor(string choice) => choice?.ToLowerInvariant() switch
    {
        Light => ThemeVariant.Light,
        Dark => ThemeVariant.Dark,
        _ => ThemeVariant.Default,
    };

    /// <summary>Anything we do not recognise counts as following the system.</summary>
    public static string Normalise(string? choice) => choice?.ToLowerInvariant() switch
    {
        Light => Light,
        Dark => Dark,
        _ => System,
    };

    public static void Apply(string choice)
    {
        if (Application.Current is { } app)
            app.RequestedThemeVariant = VariantFor(choice);
    }
}
