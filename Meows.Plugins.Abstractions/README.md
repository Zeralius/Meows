# Meows.Plugins.Abstractions

The contract a [Meows](https://github.com/Zeralius/Meows) plugin implements. Reference this package
and you can write a plugin in your own repository, with no copy of the Meows sources at all.

Meows is a Windows desktop companion built on Avalonia. It is a shell: it finds plugins, lets you
switch them on, and gives each one a tab. Everything useful is a plugin.

## Writing one

```csharp
using Avalonia.Controls;
using Meows.Plugins.Abstractions;

public sealed class MyPlugin : IMeowsPlugin
{
    public string Id => "me.my-plugin";      // stable forever: it is the settings key
    public string DisplayName => "My Plugin";
    public string Description => "One sentence, shown on the Plugins tab.";
    public string? Icon => "🎲";
    public string? Category => "Everyday";   // optional heading on the Plugins tab

    public Control CreateView(IMeowsHost host) =>
        new MyView { DataContext = new MyViewModel(host) };
}
```

Your project needs `net10.0`, and Avalonia referenced with `ExcludeAssets="runtime"`, because the
shell supplies Avalonia and the contract at runtime. Both must stay a single copy or the shell
cannot host the control you hand back.

```xml
<PackageReference Include="Avalonia" Version="12.1.1" ExcludeAssets="runtime" />
<PackageReference Include="Meows.Plugins.Abstractions" Version="0.2.1" ExcludeAssets="runtime" PrivateAssets="all" />
```

There is a `dotnet new` template that writes all of this for you:

```bash
dotnet new install Meows.Plugins.Template
dotnet new meows-plugin -n MyPlugin
```

## What the host gives you

`IMeowsHost` provides a private data directory, JSON settings, the shared log, the notification
surface, and background work the shell cancels when your plugin is switched off.

## Installing your plugin

Build it, then put the output in its own folder under the `plugins` directory beside `Meows.exe`,
named after the folder:

```
plugins/MyPlugin/MyPlugin.dll
```

Or keep it anywhere and point `MEOWS_PLUGINS_DIR` at the folder holding it, which adds to the
search rather than replacing it. `Meows.exe --list-plugins` says what was found and why anything
was refused.

## Versioning

This package carries the contract version, which moves independently of the app. The shell refuses
a plugin built against a newer contract, or a different major, and says so on the plugin's card
rather than failing later. An older minor is fine, since additions stay backward compatible.

Full documentation: **[PLUGIN-GUIDE.md](https://github.com/Zeralius/Meows/blob/main/PLUGIN-GUIDE.md)**

MIT licensed.
