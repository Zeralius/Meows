using System.ComponentModel;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meows.Plugins.Abstractions;

namespace Meows.Services;

/// <summary>
/// Every string the window can show, in every language anything has shipped.
///
/// Each assembly carries its own <c>Strings.&lt;code&gt;.json</c> as an embedded resource and they
/// are merged into one flat table, keyed by strings like <c>chonk.scan</c>. Merging rather than
/// asking each plugin at lookup time is what lets one binding in a view find a string a plugin
/// owns, and it means a plugin can be switched on halfway through without anything rewiring.
/// </summary>
public sealed partial class Translations : IMeowsText
{
    /// <summary>The languages the shell itself ships. A plugin can only translate into these.</summary>
    public static readonly string[] Shipped = ["en", "de"];

    public const string Fallback = "en";

    private readonly Dictionary<string, Dictionary<string, string>> _byLanguage =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly HashSet<Assembly> _read = [];
    private readonly Action<string>? _report;

    private string _language = Fallback;

    public Translations(Action<string>? report = null) => _report = report;

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The one Avalonia listens for on an indexer binding. Raising it says every key may have
    /// changed, which after a language switch is exactly true.
    /// </summary>
    private const string EveryKey = "Item[]";

    [GeneratedRegex(@"Strings\.([A-Za-z]{2})\.json$", RegexOptions.IgnoreCase)]
    private static partial Regex CatalogName();

    public string Language => _language;

    /// <summary>What the machine is set to, if we have it. Used for "follow the system".</summary>
    public static string SystemLanguage
    {
        get
        {
            var code = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
            return Shipped.Contains(code, StringComparer.OrdinalIgnoreCase) ? code.ToLowerInvariant() : Fallback;
        }
    }

    /// <summary>
    /// Reads an assembly's catalogues. Safe to call twice with the same assembly, because the
    /// shell calls it again every time a plugin is activated and a plugin can be switched off and
    /// back on all day.
    /// </summary>
    public void Add(Assembly assembly)
    {
        if (!_read.Add(assembly))
            return;

        foreach (var resource in assembly.GetManifestResourceNames())
        {
            var match = CatalogName().Match(resource);
            if (!match.Success)
                continue;

            try
            {
                using var stream = assembly.GetManifestResourceStream(resource);
                if (stream is null)
                    continue;

                var entries = JsonSerializer.Deserialize<Dictionary<string, string>>(stream);
                if (entries is null)
                    continue;

                var language = match.Groups[1].Value.ToLowerInvariant();
                if (!_byLanguage.TryGetValue(language, out var table))
                    _byLanguage[language] = table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var (key, value) in entries)
                    table[key] = value;
            }
            catch (Exception ex)
            {
                // A plugin with a broken catalogue falls back to English, or to bare keys. Worth
                // saying so in the log, but not worth refusing to show the plugin at all.
                _report?.Invoke($"Could not read {resource} from {assembly.GetName().Name}: {ex.Message}");
            }
        }

        // A plugin split across several assemblies keeps its strings wherever they belong, which
        // for the built-in ones means the shared disk library. Only our own are followed: every
        // assembly also references Avalonia and half of System, and none of those have catalogues.
        foreach (var reference in Referenced(assembly))
            Add(reference);

        // A plugin switched on after startup brings its own strings, and everything already on
        // screen is showing keys for them until it says so.
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(EveryKey));
    }

    private IEnumerable<Assembly> Referenced(Assembly assembly)
    {
        AssemblyName[] names;
        try
        {
            names = assembly.GetReferencedAssemblies();
        }
        catch (Exception)
        {
            yield break;
        }

        foreach (var name in names)
        {
            if (name.Name is null || !name.Name.StartsWith("Meows.", StringComparison.Ordinal))
                continue;

            Assembly loaded;
            try
            {
                // Through the load context that holds the plugin, not the shell's. A plugin's
                // private libraries sit in the plugin folder, where the default context has never
                // heard of them, so Assembly.Load finds nothing and the strings never arrive.
                loaded = (AssemblyLoadContext.GetLoadContext(assembly) ?? AssemblyLoadContext.Default)
                    .LoadFromAssemblyName(name);
            }
            catch (Exception ex)
            {
                _report?.Invoke($"Could not read strings from {name.Name}: {ex.Message}");
                continue;
            }

            yield return loaded;
        }
    }

    /// <summary>Switches language and repaints every translated string in the window.</summary>
    public void Use(string language)
    {
        var wanted = string.Equals(language, "system", StringComparison.OrdinalIgnoreCase)
            ? SystemLanguage
            : language.ToLowerInvariant();

        if (!Shipped.Contains(wanted, StringComparer.OrdinalIgnoreCase))
            wanted = Fallback;

        if (wanted == _language)
            return;

        _language = wanted;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(EveryKey));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Language)));
    }

    public string this[string key]
    {
        get
        {
            if (_byLanguage.TryGetValue(_language, out var table) && table.TryGetValue(key, out var text))
                return text;

            // An untranslated string reads in English rather than vanishing. Half a window in the
            // wrong language is a nuisance; half a window of dotted identifiers is unusable.
            if (_byLanguage.TryGetValue(Fallback, out var english) && english.TryGetValue(key, out var fallback))
                return fallback;

            return key;
        }
    }

    public string Format(string key, params object?[] values)
    {
        var template = this[key];
        try
        {
            return string.Format(CultureInfo.CurrentCulture, template, values);
        }
        catch (FormatException)
        {
            // A translation with the wrong placeholders in it. Show the template rather than
            // throwing from inside a binding, where the failure would be invisible.
            _report?.Invoke($"'{key}' does not take the values it was given in {_language}.");
            return template;
        }
    }

    /// <summary>How many keys we hold for a language. Only used to report on a catalogue.</summary>
    public int CountFor(string language) =>
        _byLanguage.TryGetValue(language, out var table) ? table.Count : 0;
}
