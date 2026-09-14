namespace Meows.Tests;

/// <summary>
/// Every plugin that ships, named once. The view smoke test, the catalogue tests, the plain-name
/// test and the search test all walk this list, and Kitten adds a line to it for each plugin it
/// writes.
///
/// Named rather than discovered because the compiler drops a project reference nothing uses, so
/// a plugin no test mentions is not even copied to the test folder. The guard in
/// <see cref="ViewSmokeTests.Every_plugin_that_ships_is_on_the_list"/> asks whether the list is
/// whole.
/// </summary>
internal static class ShippedPlugins
{
    internal static readonly Type[] Types =
    [
        typeof(Plugins.Birdwatch.BirdwatchPlugin),
        typeof(Plugins.Chonk.ChonkPlugin),
        typeof(Plugins.Collar.CollarPlugin),
        typeof(Plugins.Kibble.KibblePlugin),
        typeof(Plugins.Litter.LitterPlugin),
        typeof(Plugins.Molt.MoltPlugin),
        typeof(Plugins.Mouser.MouserPlugin),
        typeof(Plugins.Perch.PerchPlugin),
        typeof(Plugins.Portion.PortionPlugin),
        typeof(Plugins.Purrge.PurrgePlugin),
        typeof(Plugins.Saucer.SaucerPlugin),
        typeof(Plugins.Scruff.ScruffPlugin),
        typeof(Plugins.TelegramPoster.TelegramPosterPlugin),
        typeof(Plugins.Tin.TinPlugin),
        typeof(Plugins.Purr.PurrPlugin),
        typeof(Plugins.Kit.KitPlugin),
        typeof(Plugins.WeighIn.WeighInPlugin),
        typeof(Plugins.Catnip.CatnipPlugin),
        // kitten: next plugin goes here
    ];
}
