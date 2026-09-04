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
    public string? Category => "group.everyday";  // optional heading on the Plugins tab

    public Control CreateView(IMeowsHost host) =>
        new MyView { DataContext = new MyViewModel(host) };
}
```

Your project needs `net10.0`, and Avalonia referenced with `ExcludeAssets="runtime"`, because the
shell supplies Avalonia and the contract at runtime. Both must stay a single copy or the shell
cannot host the control you hand back.

```xml
<PackageReference Include="Avalonia" Version="12.1.1" ExcludeAssets="runtime" />
<PackageReference Include="Meows.Plugins.Abstractions" Version="0.4.0" ExcludeAssets="runtime" PrivateAssets="all" />
```

There is a `dotnet new` template that writes all of this for you:

```bash
dotnet new install Meows.Plugins.Template
dotnet new meows-plugin -n MyPlugin
```

## What the host gives you

`IMeowsHost` provides a private data directory, JSON settings, the shared log, the notification
surface, background work the shell cancels when your plugin is switched off, and the language the
window is in.

## Colours and language

The window is light or dark, and English or German, depending on what the person chose. Both reach
your plugin by name at run time, so neither needs a reference to the shell:

```xml
<Border Background="{DynamicResource MeowsCard}" BorderBrush="{DynamicResource MeowsLine}">
    <TextBlock Text="{m:Tr myplugin.title}" />
</Border>
```

`{m:Tr}` reads a key out of a `Strings.<code>.json` you ship as an embedded resource; `host.Text`
does the same from code. A key nobody has is shown as the key, and a key your language is missing
reads in English. A plugin that uses neither still works: it stays in English and paints its own
colours in every theme.

`{m:Tr}` follows a language change on its own. Anything your view model works out in code does
not, so hold a `LanguageWatch(OnEverythingChanged)` for its lifetime and dispose it with
everything else.

The template writes all of this out for you, and
[PLUGIN-GUIDE.md](https://github.com/Zeralius/Meows/blob/main/PLUGIN-GUIDE.md#5-colours-and-language)
lists the colour tokens.

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
