namespace Meows.Plugins.Abstractions;

/// <summary>
/// One line about a plugin for the Home tab: "1 have passed, 2 more coming up", "2 would fail
/// to post", "F: lost 40 GB since 7 Sep". <paramref name="IsTrouble"/> paints it red,
/// which is for something that wants doing, not for something that merely happened.
/// </summary>
public sealed record Glance(string Text, bool IsTrouble = false);

/// <summary>
/// Implemented by a plugin's view model to put a line of its own on the Home tab, where every
/// switched-on plugin has a card. Without this the card carries what the shell can work out
/// by itself: the plugin's last journal entry and the state of its watches. With it, the card
/// says what the plugin would say if asked "anything?", which is the question Home answers.
///
/// Read on the UI thread whenever Home is shown or refreshed, so answer from what is already
/// in memory and never from the disk or the network. Null means nothing worth a line right
/// now, and the card falls back to the shell's own words. Since 1.1.0.
/// </summary>
public interface IGlanceable
{
    Glance? Glance();
}
