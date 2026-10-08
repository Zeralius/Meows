namespace Meows.Plugins.Abstractions;

/// <summary>
/// What a plugin is about, for the filter on the Plugins tab. Flags, because plenty of plugins
/// are about two things: Nest keeps game saves (Files and Gaming), Vet looks after the family PC
/// (Files and Everyday). A plugin shows under every filter it names and always under All.
///
/// Unlike <see cref="IMeowsPlugin.Category"/>, which is free text the shell does not interpret
/// and which groups the tab strip and the headings, this is a fixed list the shell knows, so it
/// can put a translated chip up for each one. A topic nobody installed has no chip. Since 1.7.0.
/// </summary>
[Flags]
public enum PluginTopics
{
    /// <summary>Said nothing: found under All only, which is what a plugin from before 1.7.0 gets.</summary>
    None = 0,

    /// <summary>Drives, folders and files: room, duplicates, cleanup, backups.</summary>
    Files = 1 << 0,

    /// <summary>Games, their libraries, saves and screenshots.</summary>
    Gaming = 1 << 1,

    /// <summary>Pen and paper and virtual tabletops: maps, tokens, handouts.</summary>
    Tabletop = 1 << 2,

    /// <summary>The household: dates, tasks, recipes, money, habits, books.</summary>
    Everyday = 1 << 3,

    /// <summary>Posting, feeds and the bot that posts: anything with an audience.</summary>
    Social = 1 << 4,

    /// <summary>For people who write code: repositories, PATH, build output.</summary>
    Developer = 1 << 5,

    /// <summary>About Meows itself: what it watches, how it is doing.</summary>
    Meows = 1 << 6,
}
