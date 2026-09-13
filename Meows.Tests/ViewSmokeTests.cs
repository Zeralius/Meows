using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Logging;
using Avalonia.Styling;
using Avalonia.Themes.Fluent;
using Avalonia.Threading;
using Meows.Plugins.Abstractions;

[assembly: AvaloniaTestApplication(typeof(Meows.Tests.HeadlessApp))]

namespace Meows.Tests;

/// <summary>
/// The shell's resources without the shell's window: the palette, the Fluent theme, and nothing
/// that opens a main window or reads the real settings folder. Enough for a plugin's view to
/// resolve every brush and template it asks for.
/// </summary>
public sealed class HeadlessApp : Application
{
    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
        Resources.MergedDictionaries.Add(new Avalonia.Markup.Xaml.Styling.ResourceInclude(
            new Uri("avares://Meows/Themes/Palette.axaml"))
        {
            Source = new Uri("avares://Meows/Themes/Palette.axaml"),
        });
    }

    public static AppBuilder BuildAvaloniaApp() =>
        AppBuilder.Configure<HeadlessApp>()
            .UseHeadless(new AvaloniaHeadlessPlatformOptions { UseHeadlessDrawing = true });
}

/// <summary>
/// Catches what Avalonia says about bindings while a view is alive. A binding to a property
/// that is not there, a converter that throws, a template part that never resolves: none of
/// those are compile errors, all of them are one line in this log and a blank spot on screen.
/// </summary>
public sealed class BindingComplaints : ILogSink
{
    private readonly ILogSink? _inner;

    public BindingComplaints(ILogSink? inner) => _inner = inner;

    public List<string> Lines { get; } = [];

    public bool IsEnabled(LogEventLevel level, string area) =>
        level >= LogEventLevel.Warning || (_inner?.IsEnabled(level, area) ?? false);

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate)
    {
        Record(level, area, source, messageTemplate, []);
        _inner?.Log(level, area, source, messageTemplate);
    }

    public void Log(LogEventLevel level, string area, object? source, string messageTemplate, params object?[] propertyValues)
    {
        Record(level, area, source, messageTemplate, propertyValues);
        _inner?.Log(level, area, source, messageTemplate, propertyValues);
    }

    private void Record(LogEventLevel level, string area, object? source, string template, object?[] values)
    {
        if (level < LogEventLevel.Warning)
            return;

        // The two areas that mean a view is wrong. Layout and property warnings are noise here.
        if (area is not (LogArea.Binding or LogArea.Control))
            return;

        // Avalonia's templates use named holes, {Property} and the like, filled in order.
        var text = template;
        var next = 0;
        text = System.Text.RegularExpressions.Regex.Replace(text, @"\{[A-Za-z0-9_]+\}",
            _ => next < values.Length ? values[next++]?.ToString() ?? "null" : "?");

        // A path through a selection that is empty, Selected.Name with nothing selected, is
        // how every detail pane here is written, and the pane is hidden while it is so. Avalonia
        // still notes it. A property that does not exist, a converter that throws, a type that
        // will not convert: those are the lines worth failing on.
        if (text.EndsWith("Value is null.", StringComparison.Ordinal))
            return;

        Lines.Add($"[{area}] {source?.GetType().Name}: {text}");
    }
}

/// <summary>
/// Every plugin's view, built against a fake host, shown at a real size in both themes and both
/// languages, with anything Avalonia complained about turned into a failure.
///
/// This is the test the playtest checklists were standing in for. It cannot say whether a
/// layout looks right, but it says whether it holds together: every binding finds its property,
/// every template resolves, switching theme and language repaints without a throw, and disposing
/// the view lets go of everything.
/// </summary>
public class ViewSmokeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "meows-views-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    public static TheoryData<string> EveryPlugin()
    {
        var data = new TheoryData<string>();
        foreach (var type in PluginTypes)
            data.Add(type.FullName!);
        return data;
    }

    private static Type[] PluginTypes => ShippedPlugins.Types;

    [Fact]
    public void Every_plugin_that_ships_is_on_the_list()
    {
        var here = Path.GetDirectoryName(typeof(ViewSmokeTests).Assembly.Location)!;
        var built = Directory.GetFiles(here, "Meows.Plugins.*.dll")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(n => n != "Meows.Plugins.Abstractions")
            .ToHashSet(StringComparer.Ordinal);

        var listed = PluginTypes.Select(t => t.Assembly.GetName().Name!).ToHashSet(StringComparer.Ordinal);

        Assert.Empty(built.Except(listed));
    }

    [AvaloniaTheory]
    [MemberData(nameof(EveryPlugin))]
    public void The_view_holds_together_in_both_themes_and_both_languages(string typeName)
    {
        var type = PluginTypes.Single(t => t.FullName == typeName);
        var plugin = (IMeowsPlugin)Activator.CreateInstance(type)!;
        var host = new FakeHost(Path.Combine(_root, plugin.Id));

        var complaints = new BindingComplaints(Logger.Sink);
        var previousSink = Logger.Sink;
        Logger.Sink = complaints;

        Window? window = null;
        Control? view = null;
        try
        {
            view = plugin.CreateView(host);
            window = new Window { Width = 1400, Height = 900, Content = view };
            window.Show();
            Settle();

            // A view that only looks right in the theme it was written in is half a view.
            foreach (var variant in new[] { ThemeVariant.Light, ThemeVariant.Dark })
            {
                Application.Current!.RequestedThemeVariant = variant;
                Settle();
            }

            // Everything worked out in code has to follow a language change too. Both ways,
            // and back to English so the rest of the suite reads what it expects.
            var text = TestStrings.Load();
            MeowsText.Use(text);
            text.Use("de");
            Settle();
            text.Use("en");
            Settle();

            Assert.True(view.Bounds.Width > 0 && view.Bounds.Height > 0,
                $"{plugin.DisplayName}'s view laid out to nothing.");
        }
        finally
        {
            window?.Close();
            (view as IDisposable)?.Dispose();
            (view?.DataContext as IDisposable)?.Dispose();
            Settle();
            Logger.Sink = previousSink;
        }

        Assert.True(complaints.Lines.Count == 0,
            $"{plugin.DisplayName} raised {complaints.Lines.Count} complaint(s):\n" + string.Join("\n", complaints.Lines.Distinct()));
    }

    /// <summary>Lets the dispatcher run what the last change queued: layout, bindings, the lot.</summary>
    private static void Settle()
    {
        Dispatcher.UIThread.RunJobs();
        Dispatcher.UIThread.RunJobs(DispatcherPriority.Background);
    }
}
