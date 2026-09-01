# Mews

[![CI](https://github.com/Zeralius/Mews/actions/workflows/ci.yml/badge.svg)](https://github.com/Zeralius/Mews/actions/workflows/ci.yml)

A plugin-hosted desktop companion. The shell itself does almost nothing. It finds plugins, lets
you activate them, and gives each one a tab. Everything useful lives in a plugin.

Built with Avalonia 12.1.1 on .NET 10, targeting Windows.

| | |
|---|---|
| **Shell** | `Mews/` (window, tab host, plugin loader). [README](Mews/README.md) |
| **Contract** | `Mews.Plugins.Abstractions/`, the interfaces a plugin implements |
| **Plugins** | `Mews.Plugins.TelegramPoster/` [README](Mews.Plugins.TelegramPoster/README.md)<br>`Mews.Plugins.Purrge/` [README](Mews.Plugins.Purrge/README.md)<br>`Mews.Plugins.Kibble/` [README](Mews.Plugins.Kibble/README.md)<br>`Mews.Plugins.Chonk/` [README](Mews.Plugins.Chonk/README.md)<br>`Mews.Plugins.Litter/` [README](Mews.Plugins.Litter/README.md) |
| **Shared** | `Mews.Bot.Core/`, the bot's config and media rules, used by Telegram Poster and Kibble<br>`Mews.Disk/`, Recycle Bin deletion and the folder walk rules, used by Purrge, Chonk and Litter |
| **Writing one** | [PLUGIN-GUIDE.md](PLUGIN-GUIDE.md), the full contract |
| **Tests** | `Mews.Tests/`, run with `dotnet test` |

## Running it

```bash
dotnet run --project Mews/Mews.csproj
```

In Rider, set **Mews** as the startup project, but make the run configuration build the
**solution** rather than just the startup project. The shell does not reference plugin projects,
so otherwise your plugin changes are never rebuilt and you end up debugging a stale DLL.
[PLUGIN-GUIDE.md](PLUGIN-GUIDE.md#in-rider) has the details, along with the Rider XAML warnings
that turn out to be false positives.

The **Plugins** tab is always present. Toggling a plugin on builds its view and adds a tab.
Toggling it off removes the tab and disposes the view model. A plugin that throws while being
activated is caught, marked *Failed* on its card, and logged, so it cannot take the shell down.

## How plugins are found

Each plugin lives in its own subfolder of a `plugins` directory. The shell looks for that
directory in this order:

1. `MEWS_PLUGINS_DIR`, if set. It takes a `;` separated list and is **additive** rather than a
   replacement, so a plugin developed in its own repo loads alongside the built-in ones.
2. `plugins` next to the executable. This is the deployed layout, so it counts even when empty.
3. `plugins` in any parent directory. This is what makes an in-solution build
   (`Mews/bin/Debug/net10.0`) find the repo's own `plugins` folder.

Step 3 only accepts a directory that actually holds plugin-shaped content, meaning at least one
subfolder containing a `.dll`. Without that check, case-insensitive Windows paths let a *source*
folder named `Plugins` win instead. That is exactly why the shell's own loader sources live in
`Mews/PluginSystem/` rather than `Mews/Plugins/`.

Inside a plugin folder the shell prefers `<foldername>.dll` and otherwise scans every `.dll`
there, so a hand-dropped folder still works. Duplicate plugin ids are ignored with a log line.

## Writing a plugin

See **[PLUGIN-GUIDE.md](PLUGIN-GUIDE.md)** for the whole contract: project setup, `IMewsPlugin`,
every member of `IMewsHost`, notifications, background work, threading, lifetime, and the
assembly isolation rules.

The short version:

```csharp
public sealed class MyPlugin : IMewsPlugin
{
    public string Id => "mews.my-plugin";
    public string DisplayName => "My Plugin";
    public string Description => "What it does.";
    public string? Icon => "🎲";

    public Control CreateView(IMewsHost host) =>
        new MyView { DataContext = new MyViewModel(host) };
}
```

`IMewsHost` gives a plugin its own data directory, JSON settings, the shared log, the shell's
notification surface, and background work whose lifetime the shell owns.

## Versioning

Mews uses `major.minor.patch`:

| Part | Bump it for |
|---|---|
| **major** (`1.0.0`) | Big changes to the UI or the backend. Anything that reshapes how the app works or looks |
| **minor** (`0.1.0`) | A new plugin, or a substantial new capability inside one |
| **patch** (`0.0.1`) | A smaller feature, or a bug fix |

The line that needs judgement is minor against patch, since both cover new behaviour. The
question to ask is whether it changes what the app is *for*. A new plugin, or a new way of
working inside one, is minor. Another option on something that already exists is a patch.

What that has meant so far:

| Version | Change | Why |
|---|---|---|
| `0.2.0` | Kibble added | A new plugin |
| `0.3.1` | Kibble builds comics | A new way of working, not just another option |
| `0.3.3` | Sorting, and page numbers on picked files | Two smaller features on top of what was there |
| `0.1.1`, `0.1.2` | Release workflow fixes | Bug fixes |

The app version lives in `Mews/Mews.csproj` and shows at the bottom left of the window, so you
can always tell which build is running. Every change moves it, including a documentation fix
like this paragraph, so a version always points at exactly one state of the repository.

### The plugin contract is versioned separately

`Mews.Plugins.Abstractions` carries its own version and deliberately does **not** move in step
with the app. Adding a plugin is a minor app change but does not alter the contract at all, and
bumping the contract for it would make every external plugin look out of date for no reason.

So: **change the contract version only when the contract itself changes.**

| Change to the contract | Bump |
|---|---|
| A member removed, renamed or changed | **major**, since existing plugins can no longer be trusted against it |
| A member added | **minor**, since existing plugins still work but new ones need the newer shell |
| A doc or comment fix with no API change | **patch** |

This matters because the shell enforces it. `Mews.Plugins.Abstractions` is shared with the shell
rather than loaded from a plugin's folder, so .NET will happily bind a plugin compiled against a
newer contract and only fail later with a `MissingMethodException` from somewhere unhelpful.
Instead, Mews reads the contract version each plugin was compiled against and refuses anything
it cannot honour. The reason appears on the plugin's card, before a line of that plugin's code
runs:

> Built for Mews contract 0.2.0, which is newer than this shell's 0.1.0. Update Mews, or rebuild
> the plugin against 0.1.0.

A mismatched **major** is refused in either direction. A **newer** minor or patch is refused, an
older one is fine, since additive changes stay backward compatible. The Plugins tab shows which
contract version the shell provides.

## CI and releases

Two workflows, both on `windows-latest`. That is a requirement rather than a preference: the app
is a WinExe, and Purrge and Chonk delete through the Windows shell.

**[ci.yml](.github/workflows/ci.yml)** runs on every push to `main` and every pull request. It
restores, builds in Release, runs the test suite, and checks that the plugin contract still
packs, since an external plugin author depends on that package existing.

**[release.yml](.github/workflows/release.yml)** runs when you push a tag like `v0.1.0`, or
manually with a version typed in. It:

1. rejects a tag that is not `major.minor.patch`
2. runs the tests, so a failing build never ships
3. publishes the shell self-contained for win-x64, stamped with the tag version
4. builds and **stages every plugin** into `plugins/`, which publishing alone does not do
5. drops the third-party native symbols, roughly half the output
6. fails the run if `Mews.exe`, a plugin DLL, or a library a plugin needs is missing
7. zips it and attaches it to a GitHub release, along with the contract `.nupkg`

To cut a release:

```bash
git tag v0.1.0 && git push origin v0.1.0
```

People then download `Mews-0.1.0-win-x64.zip` from the releases page, unzip, and run `Mews.exe`.
Nothing else is needed, since the build is self-contained.

Step 4 is the one worth remembering. The shell does not reference plugin projects, which is what
keeps them drop-in, so a plain `dotnet publish` produces an app with no plugins at all. Step 6
exists to catch exactly that if the staging is ever broken.

## Tests

```bash
dotnet test
```

`Mews.Tests/` covers the pure and filesystem logic: group validation, config round-tripping and
next-up resolution, the staged duplicate scan, queue runway maths, file intake and its refusals,
comic bundling and its page order, disk measuring and its skip rules, contract version rules,
and token writing.
Anything needing an Avalonia render backend or a running dispatcher is deliberately out of
scope, so the suite stays headless and works on a runner.

## Where state lives

Everything sits under `%APPDATA%\Mews`:

| Path | What it holds |
|---|---|
| `activated-plugins.json` | Which plugins get a tab |
| `plugins/<plugin-id>/settings.json` | Per-plugin settings |
| `mews.log` | Truncated each run. The **Log** button shows the same lines live |

Nothing is written inside the repository, so deleting `%APPDATA%\Mews` is a clean reset.
