# Mews (the shell)

This project is the host application. It opens a window, finds plugins, lets you switch them on,
and gives each one a tab. It contains no features of its own.

If you are looking for what Mews *is*, read the [root README](../README.md). If you are writing
a plugin, read the [plugin guide](../PLUGIN-GUIDE.md). This file is about the inside of the
shell, for when you need to change it.

## Layout

| Folder | What lives there |
|---|---|
| `PluginSystem/` | Finding, vetting and loading plugin assemblies |
| `Services/` | Everything a plugin can reach through `IMewsHost`, plus the log and settings |
| `ViewModels/` | Window state: tabs, the plugin list, panel visibility |
| `Views/` | The window and the Plugins tab |

The loader lives in `PluginSystem/` rather than a folder called `Plugins/` on purpose. Windows
path matching ignores case, so a folder named `Plugins` sitting in the source tree gets picked
up by the discovery probe as if it were the real plugin directory. Renaming it removed that
trap. There is a second guard in the probe itself, but the rename is what stops the confusion
happening in the first place.

## Startup

`Program.Main` builds the Avalonia app. `App.OnFrameworkInitializationCompleted` then wires
everything together in one place:

```
ShellSettings      %APPDATA%\Mews, activation list, per-plugin settings
ShellLog           in-memory lines plus mews.log
NotificationCenter the shared alert surface
BackgroundTaskService  owns every plugin's background work
PluginCatalog      discovery
MainWindowViewModel   ties them together
```

`MainWindowViewModel.Initialize` adds the permanent **Plugins** tab, then scans. That tab is
always index zero, so an empty plugins folder still lands somewhere sensible.

## Loading a plugin

1. `PluginCatalog.ResolvePluginsDirectories` works out where to look. `MEWS_PLUGINS_DIR` first
   (a `;` separated list, added to rather than replacing the rest), then a `plugins` folder
   beside the executable, then one in any parent directory.
2. Each subfolder is one plugin. The catalog prefers `<foldername>.dll` and otherwise scans
   every `.dll` in there.
3. `PluginLoadContext` loads it in its own `AssemblyLoadContext`. Anything starting with
   `Mews.Plugins.Abstractions`, `Avalonia`, `System.` or `Microsoft.` is shared with the shell
   instead of loaded privately. That sharing is not an optimisation. A plugin holding its own
   copy of Avalonia would hand back a `Control` of a type the shell cannot host.
4. `ContractCompatibility` reads the contract version the assembly was compiled against and
   refuses anything the shell cannot honour. This happens before any of the plugin's types are
   touched, so a plugin that will not work never runs a line of code.
5. Surviving types that implement `IMewsPlugin` and have a parameterless constructor are
   instantiated into a `PluginDescriptor`.

A `PluginDescriptor` is either a loaded plugin or a refusal carrying a reason. The Plugins tab
renders both, so an incompatible plugin is visible rather than silently missing.

## Activation and teardown

Switching a plugin on builds a `PluginHost` scoped to it, calls `CreateView`, and adds a tab.
Anything thrown there is caught, shown as *Failed* on the card, and logged. A broken plugin
cannot take the window down.

Switching it off does three things in order, and the order matters:

1. cancel that plugin's background work
2. retract its notifications
3. remove the tab and dispose the view and its `DataContext`

Doing it the other way round would leave a cancelled task posting a notification into a shell
that had already forgotten the plugin existed.

## Services

**`ShellSettings`** owns `%APPDATA%\Mews`. Activation list, one data directory per plugin, JSON
settings. Nothing is written inside the repository, so deleting that folder is a clean reset.

**`ShellLog`** keeps a bounded list of lines for the Log pane and appends to `mews.log`, which is
truncated each run. It is a "what just happened" log, not an audit trail.

**`NotificationCenter`** holds events and conditions. Conditions are keyed per plugin, so
re-posting the same key replaces the entry instead of stacking. Only the plugin that raised a
condition can clear it, because only it knows whether the condition still holds.

**`BackgroundTaskService`** owns the lifetime of every plugin's background work through a
cancellation source per plugin, linked to an app-wide one. A fault becomes an error notification
rather than an unobserved task exception.

Both of the last two marshal to the UI thread themselves, so a plugin can call them from
anywhere.

## Things worth knowing before you change something

The shell does not reference any plugin project. That is what keeps plugins drop-in, and it has
a consequence: building the startup project alone does not rebuild plugins. Build the solution.

Plugins are not hot-reloaded. Restart after building.

`Plugins` is both a namespace and a property on `MainWindowViewModel`, so writing
`Plugins.ContractCompatibility` inside that class resolves to the property and fails to compile.
Use the bare type name.
