using Meows.Plugins.Abstractions;

namespace Meows.ViewModels;

/// <summary>
/// A button that asks before it does something nothing brings back: the first press changes its
/// words, the second does it. A second press within half a second is the same double-click and
/// does not count, and one more than ten seconds later is somebody who forgot it was armed, so it
/// arms again rather than doing it.
/// </summary>
public sealed class TwoStep : ObservableObject
{
    private static readonly TimeSpan TooSoon = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan TooLate = TimeSpan.FromSeconds(10);

    private readonly Func<string> _plain;
    private readonly Func<string> _sure;
    private readonly Func<DateTime> _now;
    private bool _isArmed;
    private DateTime _armedAt;

    public TwoStep(Func<string> plain, Func<string> sure, Func<DateTime>? now = null)
    {
        _plain = plain;
        _sure = sure;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public bool IsArmed
    {
        get => _isArmed;
        private set
        {
            if (SetField(ref _isArmed, value))
                OnPropertyChanged(nameof(Text));
        }
    }

    /// <summary>What the button says: the verb, or the question once armed.</summary>
    public string Text => _isArmed ? _sure() : _plain();

    /// <summary>A press. True when this one should do the thing.</summary>
    public bool Press()
    {
        var now = _now();
        if (!_isArmed)
        {
            _armedAt = now;
            IsArmed = true;
            return false;
        }

        var since = now - _armedAt;
        if (since < TooSoon)
            return false;
        if (since > TooLate)
        {
            _armedAt = now;
            return false;
        }

        IsArmed = false;
        return true;
    }

    /// <summary>Back to the plain verb, for when something else happened in between.</summary>
    public void Disarm() => IsArmed = false;

    /// <summary>The words again, after a language change or a change in what they name.</summary>
    public void Reword() => OnPropertyChanged(nameof(Text));
}
