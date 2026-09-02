# Mews

[![CI](https://github.com/Zeralius/Mews/actions/workflows/ci.yml/badge.svg)](https://github.com/Zeralius/Mews/actions/workflows/ci.yml)

A desktop companion for the jobs that usually mean a terminal, a file manager and a lot of
clicking. Mews itself is just a shell: it finds plugins, lets you switch them on, and gives each
one a tab. Everything useful is a plugin, and you only turn on the ones you want.

Windows, built with Avalonia on .NET 10. The download is self contained, so there is no runtime
to install.

## What comes with it

| Plugin | What it does |
|---|---|
| **[Chonk](Mews.Plugins.Chonk/README.md)** | Measures where the room on a drive went, biggest first, says what each folder actually is, and clears out what you no longer want |
| **[Purrge](Mews.Plugins.Purrge/README.md)** | Finds files with identical content anywhere on the machine, groups them, and removes the copies you do not want |
| **[Molt](Mews.Plugins.Molt/README.md)** | Sheds caches and build output that can be rebuilt, and tells you what losing each one costs first |
| **[Mouser](Mews.Plugins.Mouser/README.md)** | Hunts down dead weight: empty folders, empty files, shortcuts pointing at things that are gone |
| **[Litter](Mews.Plugins.Litter/README.md)** | Sorts out the downloads folder by age and kind, and calls out the downloads that never finished |
| **[Saucer](Mews.Plugins.Saucer/README.md)** | Keeps what you copy, images included, and drops images into a folder for sorting |
| **[Kibble](Mews.Plugins.Kibble/README.md)** | Sorts a folder of new material into queues, one key press at a time, and can bundle a pick into a comic |
| **[Telegram Poster](Mews.Plugins.TelegramPoster/README.md)** | Drives a [Telegram posting bot](https://github.com/Zeralius/telegram-posting-bot): its groups, queues and schedule |

The last two are built around a specific posting bot. The rest are general purpose.

## Getting it

Download `Mews-<version>-win-x64.zip` from the
[releases page](https://github.com/Zeralius/Mews/releases), unzip it anywhere, and run
`Mews.exe`. Nothing else is needed.

**Extract the zip properly before running it.** Launching `Mews.exe` from inside the archive
makes your unzip tool copy it to a temporary folder on its own, and some of the plugins lose
files they need. Mews will start, but those plugins refuse to open and say so on their card.

Every plugin starts switched off. Open the **Plugins** tab, turn on the ones you want, and each
gets its own tab. Turning one off closes its tab again. Nothing runs until you ask for it.

Settings and logs live in `%APPDATA%\Mews`, never inside the folder you unzipped, so deleting
that folder resets Mews completely.

## Building it yourself

```bash
git clone https://github.com/Zeralius/Mews.git
cd Mews
dotnet run --project Mews/Mews.csproj
```

You need the .NET 10 SDK. In Rider or Visual Studio, set **Mews** as the startup project and make
the run configuration build the **whole solution**: the shell does not reference the plugin
projects, so building only the startup project leaves you debugging yesterday's plugin DLLs.
[PLUGIN-GUIDE.md](PLUGIN-GUIDE.md#in-rider) covers that, along with some Rider XAML warnings that
are false positives.

```bash
dotnet test
```

The suite covers the filesystem and logic layers: duplicate scanning, disk measuring, queue
maths, file intake and its refusals, comic page ordering, clipboard conversion, cache
cataloguing, shortcut parsing, and the plugin contract rules. Anything needing a render backend is
out of scope, so it stays headless and runs on CI.

## Writing a plugin

A plugin is a class library that references `Mews.Plugins.Abstractions` and exports one type:

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

`IMewsHost` gives you a private data directory, JSON settings, the shared log, the notification
surface, and background work the shell cancels for you.

Drop the built DLL into a folder under `plugins/` and Mews picks it up. You do not need to fork
this repository: set `MEWS_PLUGINS_DIR` to your own folder and your plugin loads alongside the
built in ones.

**[PLUGIN-GUIDE.md](PLUGIN-GUIDE.md)** is the full contract: project setup, every member of
`IMewsHost`, notifications, background work, threading, lifetime, packaging and the assembly
isolation rules.

## How plugins are found

Each plugin lives in its own subfolder of a `plugins` directory. Mews looks for one:

1. in `MEWS_PLUGINS_DIR`, if set, which takes a `;` separated list and adds to the search rather
   than replacing it
2. next to `Mews.exe`, which is the layout in the release zip
3. in any parent directory, which is what makes a source build find the repository's own folder

Inside a plugin folder, Mews prefers `<foldername>.dll` and otherwise scans every `.dll` there.
Duplicate plugin ids are ignored, and a plugin that throws while starting up is caught, marked
**Failed** on its card and logged, so a broken plugin cannot take the app down with it.

## Repository layout

| | |
|---|---|
| `Mews/` | The shell: window, tab host, plugin loader. [README](Mews/README.md) |
| `Mews.Plugins.Abstractions/` | The contract a plugin implements, published as a NuGet package |
| `Mews.Plugins.*/` | The plugins listed above |
| `Mews.Bot.Core/` | Shared: the posting bot's config and media rules |
| `Mews.Disk/` | Shared: Recycle Bin deletion and folder walk rules |
| `Mews.Tests/` | The test suite |

## Versioning

`major.minor.patch`, where **major** is a change to how the app works or looks, **minor** is a
new plugin or a substantial new capability inside one, and **patch** is a smaller feature or a
bug fix.

The app version is in `Mews/Mews.csproj` and shows at the bottom left of the window, so you can
always tell which build you are running.

`Mews.Plugins.Abstractions` carries its own version and does **not** move with the app, so adding
a plugin does not make every external plugin look out of date. Its version changes only when the
contract itself does: **major** if a member is removed or changed, **minor** if one is added,
**patch** for documentation.

Mews checks that version when it loads a plugin and refuses anything it cannot honour, with the
reason on the plugin's card rather than a crash later:

> Built for Mews contract 0.2.0, which is newer than this shell's 0.1.0. Update Mews, or rebuild
> the plugin against 0.1.0.

A newer contract is refused; an older one is fine, since additions stay backward compatible.

## CI and releases

Both workflows run on `windows-latest`, which is required rather than preferred: the app is a
WinExe and several plugins delete through the Windows shell.

**[ci.yml](.github/workflows/ci.yml)** runs on every push to `main` and every pull request. It
builds in Release, runs the tests, and checks that the plugin contract still packs.

**[release.yml](.github/workflows/release.yml)** runs when a `v*` tag is pushed, or manually with
a version typed in. It runs the tests first, publishes the shell self contained for win-x64,
stages every plugin into `plugins/`, drops the third party native symbols, checks that nothing is
missing from the package, then zips it and attaches it to a GitHub release along with the
contract `.nupkg`.

Staging the plugins is a separate step because the shell does not reference the plugin projects,
which is what keeps them drop in. A plain `dotnet publish` would produce an app with no plugins
at all, so the workflow verifies every plugin DLL and its dependencies are present before
publishing.

To cut a release:

```bash
git tag v0.7.0 && git push origin v0.7.0
```

## Licence

Not yet chosen. Ask before reusing anything here.
