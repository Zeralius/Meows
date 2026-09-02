# Mews plugin template

A `dotnet new` template for a [Mews](https://github.com/Zeralius/Mews) plugin. It writes the
project, the plugin class, a view and a view model, ready to build and drop into a plugins folder.

```bash
dotnet new install Mews.Plugins.Template
dotnet new mews-plugin -n WeatherWatch
```

That gives you:

```
WeatherWatch/
  WeatherWatch.csproj          net10.0, Avalonia and the contract, both runtime excluded
  WeatherWatch.cs              the IMewsPlugin implementation
  WeatherWatchView.axaml       the tab
  WeatherWatchView.axaml.cs
  WeatherWatchViewModel.cs     with the host wired in
```

The plugin id is taken from the name in lower case. Change it before anyone installs the plugin:
it is the settings key, so moving it later orphans whatever was stored.

`--Category "Everyday"` sets the heading it appears under on the Plugins tab. Leave it or pick
your own; the shell keeps no list of valid ones.

## Building and installing it

```bash
dotnet build -c Release -o path/to/plugins/WeatherWatch
```

The output folder must be named after the dll. Put it under the `plugins` directory beside
`Mews.exe`, or point `MEWS_PLUGINS_DIR` at a folder of your own, which adds to the search rather
than replacing it.

```bash
Mews.exe --list-plugins
```

says what was found and why anything was refused.

## Why both references exclude the runtime

The shell supplies Avalonia and the contract at run time, and there has to be exactly one copy of
each. A plugin carrying its own Avalonia hands back a `Control` of a type the shell cannot host.
Mews checks the version of both when it loads a plugin and refuses anything it cannot honour, with
the reason on the plugin's card rather than a crash later.

Full documentation: **[PLUGIN-GUIDE.md](https://github.com/Zeralius/Mews/blob/main/PLUGIN-GUIDE.md)**

MIT licensed.
