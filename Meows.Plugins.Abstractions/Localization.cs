using System.ComponentModel;
using Avalonia.Data;
using Avalonia.Markup.Xaml;

namespace Meows.Plugins.Abstractions;

/// <summary>
/// The strings for whichever language is switched on. Keys are namespaced by whoever owns them,
/// so the shell uses <c>shell.*</c> and a plugin uses its own prefix.
/// </summary>
public interface IMeowsText : INotifyPropertyChanged
{
    /// <summary>
    /// The translation, or the English one if this language has not got it, or the key itself if
    /// nobody has. Never null and never throws: a missing string should look wrong on screen, not
    /// take the window down.
    /// </summary>
    string this[string key] { get; }

    /// <summary>
    /// Fills the placeholders in a translated string. Word order differs between languages, so
    /// the translation owns the whole sentence and the numbers are dropped into it, rather than
    /// the code gluing fragments together.
    /// </summary>
    string Format(string key, params object?[] values);

    /// <summary>Two letter code of the language in use, once "follow the system" is resolved.</summary>
    string Language { get; }
}

/// <summary>
/// One key, as something a binding can sit on.
///
/// Binding straight to the string table's indexer looks tidier and does not work: Avalonia reads
/// the value once and never hears that the language changed, so the window stays in whatever it
/// started in until it is reopened. An ordinary property on an ordinary object it does hear about,
/// which is all this is.
/// </summary>
public sealed class TranslatedString : INotifyPropertyChanged
{
    private readonly IMeowsText _text;

    internal TranslatedString(IMeowsText text, string key)
    {
        _text = text;
        Key = key;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public string Key { get; }

    public string Value => _text[Key];

    internal void Refresh() => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
}

/// <summary>
/// Where <see cref="TrExtension"/> and plugin code find the active language.
///
/// A static because the contract assembly is shared with the shell and every plugin, so there is
/// exactly one of these per process, which is also how many languages the window is in.
/// </summary>
public static class MeowsText
{
    /// <summary>
    /// Stands in front of the real catalogue for the life of the process.
    ///
    /// Bindings hold onto whatever object they were given, so the thing they are given has to
    /// outlive every language change and every reload. This forwards to whatever the shell has
    /// installed, which means a binding made before the shell got round to loading the strings
    /// still updates when it does.
    /// </summary>
    private sealed class Front : IMeowsText
    {
        private readonly Dictionary<string, TranslatedString> _watched = new(StringComparer.Ordinal);
        private IMeowsText? _real;

        public event PropertyChangedEventHandler? PropertyChanged;

        public void Attach(IMeowsText text)
        {
            if (_real is not null)
                _real.PropertyChanged -= Forward;

            _real = text;
            text.PropertyChanged += Forward;

            // Everything on screen was translated by whoever came before.
            Forward(this, new PropertyChangedEventArgs("Item[]"));
        }

        /// <summary>
        /// One of these per distinct key rather than per use of it, so a key on twenty cards is
        /// twenty bindings watching one object.
        /// </summary>
        public TranslatedString Entry(string key)
        {
            lock (_watched)
            {
                if (!_watched.TryGetValue(key, out var entry))
                    _watched[key] = entry = new TranslatedString(this, key);
                return entry;
            }
        }

        private void Forward(object? sender, PropertyChangedEventArgs e)
        {
            PropertyChanged?.Invoke(this, e);

            TranslatedString[] all;
            lock (_watched)
                all = _watched.Values.ToArray();

            foreach (var entry in all)
                entry.Refresh();
        }

        /// <summary>The key itself until the shell has loaded anything, which is honest enough.</summary>
        public string this[string key] => _real is null ? key : _real[key];

        public string Format(string key, params object?[] values) =>
            _real is null ? key : _real.Format(key, values);

        public string Language => _real?.Language ?? "en";
    }

    public static IMeowsText Current { get; } = new Front();

    /// <summary>Called by the shell. Plugins read <see cref="Current"/> and leave this alone.</summary>
    public static void Use(IMeowsText text) => ((Front)Current).Attach(text);

    /// <summary>The bindable form of one key. Used by <see cref="TrExtension"/>.</summary>
    public static TranslatedString Entry(string key) => ((Front)Current).Entry(key);
}

/// <summary>
/// Tells a view model when the language changed.
///
/// <c>{m:Tr}</c> in a view looks after itself, because that is a binding to a string that says
/// when it changed. Anything a view model works out in code is a different matter: the property
/// would return the new language perfectly well, but nothing asks it to, so the window keeps
/// showing what it read the first time.
///
/// Hold one of these for the life of the view model and dispose it with everything else:
///
/// <code>
/// _language = new LanguageWatch(OnEverythingChanged);
/// </code>
/// </summary>
public sealed class LanguageWatch : IDisposable
{
    private readonly Action _changed;
    private bool _finished;

    public LanguageWatch(Action changed)
    {
        _changed = changed;
        MeowsText.Current.PropertyChanged += OnTextChanged;
    }

    private void OnTextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (!_finished)
            _changed();
    }

    /// <summary>
    /// Unhooks. The string table outlives every plugin, so a view model that forgets this stays
    /// alive through it, along with the whole tab hanging off it.
    /// </summary>
    public void Dispose()
    {
        _finished = true;
        MeowsText.Current.PropertyChanged -= OnTextChanged;
    }
}

/// <summary>
/// Looks a string up from XAML: <c>Text="{m:Tr chonk.scan}"</c>.
///
/// Returns a binding rather than the string, so switching language redraws the window instead of
/// needing a restart.
/// </summary>
public sealed class TrExtension : MarkupExtension
{
    public TrExtension()
    {
    }

    public TrExtension(string key) => Key = key;

    public string Key { get; set; } = "";

    public override object ProvideValue(IServiceProvider serviceProvider) => new Binding
    {
        Source = MeowsText.Entry(Key),
        Path = nameof(TranslatedString.Value),
        Mode = BindingMode.OneWay,
    };
}
