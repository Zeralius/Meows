<p align="center">
  <img src="Meows/Assets/meows.png" width="128" alt="The Meows cat">
</p>

# Meows

[![CI](https://github.com/Zeralius/Meows/actions/workflows/ci.yml/badge.svg)](https://github.com/Zeralius/Meows/actions/workflows/ci.yml)

A desktop companion for the jobs that usually mean a terminal, a file manager and a lot of
clicking. Meows itself is just a shell: it finds plugins, lets you switch them on, and gives each
one a tab. Everything useful is a plugin, and you only turn on the ones you want.

Windows, built with Avalonia on .NET 10. The download is self contained, so there is no runtime
to install.

## What comes with it

| Plugin | What it does |
|---|---|
| **[Chonk](Meows.Plugins.Chonk/README.md)** | Measures where the room on a drive went, biggest first, says what each folder actually is, notices an archive sitting beside its own extracted contents, and clears out what you no longer want |
| **[Purrge](Meows.Plugins.Purrge/README.md)** | Finds files with identical content anywhere on the machine, groups them, and removes the copies you do not want; also checks that a backup copy is really complete and identical, without touching either side |
| **[Molt](Meows.Plugins.Molt/README.md)** | Sheds caches and build output that can be rebuilt, and tells you what losing each one costs first |
| **[Mouser](Meows.Plugins.Mouser/README.md)** | Hunts down dead weight: empty folders, empty files, shortcuts pointing at things that are gone |
| **[Litter](Meows.Plugins.Litter/README.md)** | Sorts out the downloads folder by age and kind, and calls out the downloads that never finished |
| **[Saucer](Meows.Plugins.Saucer/README.md)** | Keeps what you copy, images included, and drops images into a folder for sorting |
| **[Birdwatch](Meows.Plugins.Birdwatch/README.md)** | Watches accounts on Bluesky, Mastodon and Reddit, and any RSS feed with pictures in it, and saves what they post into that same folder, with no login anywhere |
| **[Tin](Meows.Plugins.Tin/README.md)** | Reads the exports and statements your bank hands out, CSV or PDF, and says what is recurring, what quietly went up, and what is still going out for something you stopped using |
| **[Collar](Meows.Plugins.Collar/README.md)** | Keeps the dates that matter and the paper behind them, and says so on the notification surface when one comes round |
| **[Scruff](Meows.Plugins.Scruff/README.md)** | Takes the metadata out of pictures, posts them to Bluesky, Mastodon, Discord, DeviantArt and Tumblr, and hands them to FurAffinity, X, Instagram and Reddit with every field spelled that site's way |
| **[Purr](Meows.Plugins.Purr/README.md)** | Says what Meows is watching, when it last looked, and which watch quietly stopped |
| **[Familiar](Meows.Plugins.Familiar/README.md)** | The wizard's cat for the table: maps gridded or gridless, tokens cut round, handouts framed, on the bench or as a one-shot kit with its run sheet, handed to Foundry or Roll20 the way each wants it |
| **[Weigh-In](Meows.Plugins.WeighIn/README.md)** | Measures every drive once a day and says what grew: which drive lost how much since last week, and the folders responsible |
| **[Catnip](Meows.Plugins.Catnip/README.md)** | Finds what was downloaded and never opened, least recently touched first, without opening anything itself |
| **[Rehome](Meows.Plugins.Rehome/README.md)** | Packs the machine up before a clean Windows install: what is installed and how to get it back, what the wipe takes and where to keep it, and the way back afterwards |
| **[Scoop](Meows.Plugins.Scoop/README.md)** | Every drive's Recycle Bin in one list: what is in it, how big, how old, put back one at a time or emptied per drive |
| **[Trail](Meows.Plugins.Trail/README.md)** | PATH read and explained: entries pointing nowhere, the same tool on there twice, and which one actually wins |
| **[Larder](Meows.Plugins.Larder/README.md)** | Every installed Steam game with its size, when it was last played and which library it is in, the never-touched largest first |
| **[Carry](Meows.Plugins.Carry/README.md)** | Moves a folder to a drive with room, checks every file, and leaves a junction behind so every path that pointed at it still works |
| **[Cattery](Meows.Plugins.Cattery/README.md)** | Every git repository under your project folders: its branch, uncommitted work, what is not pushed, and how long since anyone committed, the most neglected first |
| **[Nest](Meows.Plugins.Nest/README.md)** | Game saves, maps, project files and keys: the few things that cannot be downloaded again, how much there is, and how long since each was copied somewhere else |
| **[Kibble](Meows.Plugins.Kibble/README.md)** | Sorts a folder of new material into queues, one key press at a time, and can bundle a pick into a comic |
| **[Perch](Meows.Plugins.Perch/README.md)** | One timeline of what the posting bot will send and when, every group merged, out as far as the queues last |
| **[Portion](Meows.Plugins.Portion/README.md)** | Finds what the bot will fail on in every queue before it fails at night, and shrinks the pictures that can be shrunk |
| **[Telegram Poster](Meows.Plugins.TelegramPoster/README.md)** | Drives a [Telegram posting bot](https://github.com/Zeralius/telegram-posting-bot): its groups, queues and schedule, including slowing a group down so a short queue lasts |

The last four are built around a specific posting bot. The rest are general purpose.

## Getting it

Download `Meows-<version>-win-x64.zip` from the
[releases page](https://github.com/Zeralius/Meows/releases), unzip it anywhere, and run
`Meows.exe`. Nothing else is needed. When a newer one is on that page, the status bar says so,
once a day, with the page one click away; Meows does not replace itself underneath you.

Settings live in `%APPDATA%\Meows`, so the unzipped folder can be thrown away and replaced.
For a stick, or a folder that moves between machines, put an empty file called `portable` (or
`portable.txt`) next to `Meows.exe` and start it again: everything then lives in `data\` beside
the exe and nothing is written to the profile. The Settings tab says which of the two it is.

**Extract the zip properly before running it.** Launching `Meows.exe` from inside the archive
makes your unzip tool copy it to a temporary folder on its own, and some of the plugins lose
files they need. Meows will start, but those plugins refuse to open and say so on their card.

The window opens on **Home**: what happened while it was away. The notifications that are up,
with their buttons; what is running and how many schedules are watching; each switched-on
plugin's card with its tab a click away; and what the plugins did since the window was last
hidden. A plugin that has one sentence to say puts it on its card, "1 have passed, 2 more
coming up", "2 would fail to post", red when it wants doing; under it is what the plugin last
did and whether its watches are running. Meows lives in the tray, so this is the page for opening
it after hours.

**This week**, on Home, counts the last seven days of the history: how many things were done, what
went to the Recycle Bin, what Carry moved to another drive, how much rules set off, and the five
things done most, in each plugin's own words. Once a week the same comes as one notification,
unless the Settings tab turns it off; the first week only starts the clock. Every number is read
back from what the plugins already recorded, so it is exactly as true as the history.

Every plugin starts switched off. Open the **Plugins** tab, turn on the ones you want, and each
gets its own tab. Turning one off closes its tab again. Nothing runs until you ask for it.

Each switched-on plugin's card also says what it has cost since Meows started: how long its tab
took to open, the part of startup it is responsible for, marked when that passed half a second;
and how many background runs it did, how long they took together and the longest of them. Memory
is not on it on purpose: every plugin shares one process, and a number that cannot be put down to
one plugin honestly is worse than none.

**Tabs come in groups.** Each group is a coloured chip on the strip with its tabs after it, and
clicking the chip shuts the group behind it, which is what keeps twenty-odd tabs on one row. The
groups are the same ones the Plugins tab has always used, the category each plugin declares for
itself, so a fresh install is already sorted: the disk tools together, the four built around the
posting bot together, Meows' own tabs under one chip. Home is never in a group and always first.

**Drag a tab** anywhere on the strip: a line shows which side of what it would land on, and
letting go on another group's tabs or on its chip moves it into that group. **Drag a chip** to
move a whole group along the strip. Nothing moves until the pointer has travelled far enough to
mean it, so a slightly shaky click still just selects the tab.

Right-click a chip to rename the group, give it one of seven colours, or move it along the strip.
Right-click a tab to step it left or right, drop it into another group, switch the plugin off,
or start a group of its own; a tab stepped off the end of its group joins the next one. *Put it back where its plugin
says* undoes it for one tab, and the palette has the same for all of them. What you arrange is
remembered, and a group whose plugins are all switched off waits rather than showing empty.

Any tab can go into a window of its own: the small **⧉** on its header. Familiar's map on the
television, the run sheet on the laptop, without a second application. Where each window sat
is remembered per monitor layout, so the tab that lived on the second screen goes back there
when that screen is plugged in, and which tabs were out is remembered across a restart.
**Bring it back** where the tab was, or close the window. The main window is remembered the
same way.

**Keys.** Ctrl+K is the palette: a plugin by either of its names, a setting, something an open
plugin is showing, a line from the history. Ctrl+Shift+K is the same box over only the tab in
front. Ctrl+1 to Ctrl+9 pick a tab in the order they are shown, counting only the tabs that can
be seen, so a shut group does not make the numbers skip. In the palette, `>` lists the
things to do rather than the places to go: pop a tab out or bring it back, the theme, the
language, the names, rescan the plugins folder.

Starting `Meows.exe` while one is already running does not start a second: the one that is
running shows its window and the second start ends.

The cards sit under headings a plugin picks for itself, so the disk tools are together and the two
built around the posting bot are together. **Install plugin…** on that tab takes a plugin
someone else built, as a zip, or drop the zip on the tab; its card then says who made it and
offers **Uninstall**, and once a day Meows looks at the plugin's repository for a newer release
and offers that on the card too. **Open plugins folder** takes you to where they are read from.

The **Settings** tab has two choices, and both take effect as you make them:

- **Tab strip**: compact, normal or large. How big the tabs and group chips along the top are
  drawn; larger ones are easier to hit and take another row when the strip wraps.
- **Theme**: light, dark, or follow the system. Following the system means Meows changes with
  Windows, including when Windows switches itself at sunset.
- **Language**: English, German, or follow the system. It applies to the shell and to every plugin
  that ships the language. Anything a plugin has not translated stays in English rather than
  disappearing.
- **In the background**: Meows sits in the notification area, and closing the window hides it
  rather than quitting. Everything that watches or waits, Collar's dates, Birdwatch's refresh, a
  scan you started, carries on while the window is hidden, and the icon shows a dot when there is
  something to read and a ring while something is still working. *Quit* is in the icon's menu. Untick the setting and the close button quits,
  as it did before 2.0.
- **Server**: the machine the bot and Foundry run on, for the plugins that can put something
  there. A folder, meaning a share, a mapped drive or anything Windows can already open, or SFTP
  with an SSH key, kept sealed to your Windows account rather than pointed at. For SFTP, **Test**
  shows the key the server answers with; compare it with the server and press **Trust**, and
  from then on a server answering with any other key is refused. Nothing is copied until a
  plugin asks. Familiar is the first to ask: a Foundry export goes to the server too, straight
  into the meows-kit module's kits folder.
- **Starting up**: whether Meows starts when you log in, and whether it starts in the tray or
  with the window open. It writes one entry to the per-user startup list, which needs no admin
  rights and shows up in the Task Manager's Startup tab like anything else.

**Ctrl+K** opens the command palette: type a plugin under either of its names, a setting, a
word from the history, or anything an open plugin is showing, a file in Kibble's grid, a folder
Chonk measured, a date in Collar, a payee in Tin, and Enter lands on it. Every plugin is named after the cat; each also says what
it is in plain words, and the paw in the bottom bar, or the Settings tab, picks which name is on
the tabs and cards: Purrge or Duplicates, Chonk or Disk usage, Kibble or Sorting.

The **History** tab is what the plugins did, all of them in one list: what Kibble queued where,
what Purrge recycled, what Scruff posted, what Portion shrank, which date Collar dealt with. It
is kept in a small database beside the settings and survives the window closing, the plugin
being switched off, and the undo list being emptied. A line the plugin that wrote it can still
reverse, a Kibble send whose file is still in the queue, carries a **Put back** button while that
plugin is open; the shell asks the plugin first, so the button is only there when pressing it
would do something. The tab's bottom bar says how big the database file is and how many lines it
holds, with **Forget…** older than a month, three months, six months, a year or everything, for
every plugin or only the one being shown, which asks first and says how many lines would go, and
**Compact**, which gives the room back. The Settings tab has the standing rule, **keep everything**
by default, or a year, six months, three months or a month, applied when Meows starts and once a
day while it runs; Purr lists that pass under Meows. Neither forgetting ever touches the facts a
plugin keeps or the hashes anything has seen.

The **Rules** tab joins the plugins up: *when* one plugin records something, *then* ask another
to act. When Birdwatch saves a picture, check Portion's queues; when Kibble queues a file, have
Scruff take the metadata out of it where it now sits; when Purrge sends a copy to the bin, put
it on Collar's list for a week from now. A rule is one sentence of dropdowns, with an optional
word the event has to mention, and runs whether or not the window is open: a plugin a rule asks
is switched on if it is off, and its tab is not brought to the front. Every firing is a line in
the History tab under *Instinct*, saying what fired, what was asked and what came back, so the
first surprising night can be read rather than guessed at.

Three things keep it from being the surprise. **One hop**: whatever a plugin does because a rule
asked never starts another rule, so nothing can set off a chain. **One at a time**: five pictures
saved in a burst are five checks in a row, not five at once. **A rule that runs away is paused**:
more than thirty firings in ten minutes stops it with the reason on its row and a *Resume*
button. A rule whose plugin has been uninstalled is never dropped; its row says what is missing,
and it runs again the day the plugin is back.

What can be asked for so far: Collar puts it on the list, for today or a week out; Portion checks
the queues; Purrge looks for duplicates in the folder; Scruff cleans the picture in place, the
original to the Recycle Bin and the clean copy keeping its date so a queue keeps its order; and
Weigh-In takes a reading. Anything any plugin records can start a rule, and every plugin that
writes to the history says in words what it records, so a rule can wait for something that has
not happened yet.

Every card on the **Plugins** tab carries a line of health: the last thing that plugin recorded
and how long ago, and how many of its watches are running, a stopped one in red.

The **Log** tab is what Meows and the plugins said this run, with a word to filter on, a source
to narrow to, *Only trouble* to see the warnings and errors, and on the right a level per source:
everything, warnings and errors, or nothing, which is quiet mode for a plugin with a lot to say.
The bottom pane shows the same lines under the same levels; `meows.log` keeps every line
regardless.

The Settings tab's **Moving house** section exports all of that as one zip and imports it again on
another machine: preferences, which plugins are on, the bot folder, the history, every plugin's
settings and data, never the secrets and never the log. An import keeps what it writes over as a
bundle of its own beside the settings, so it can be undone by importing that.

Settings, logs and that history live in `%APPDATA%\Meows`, never inside the folder you unzipped,
so deleting that folder resets Meows completely. `meows.log` there is also where a crash gets written, stack
trace and all, which is worth attaching to a bug report. The log stays in English whatever the
window is set to, because it is the thing that gets pasted into a bug report.

To check an install without opening the window:

```bash
Meows.exe --list-plugins
```

It lists what the shell can find, flags anything refused on contract grounds or missing a private
library, and exits non zero if either happened. This is what the release build runs against the
package it just made.

To see the Home tab without the window:

```bash
Meows.exe --glance
Meows.exe --glance --json
```

Every switched-on plugin's line, in the window's language, a `!` in front of whatever wants
doing: Collar's dates, Weigh-In's last reading, Trail's PATH when something on it is not doing
anything, and for the rest the last thing each recorded. `--json` is the same for a status bar,
a terminal greeting or a scheduled task, with a `trouble` flag at the top and each plugin's id.
Nothing is switched on or started, and it runs beside a Meows that is already open.

To have a plugin do its work with no window, from Task Scheduler:

```bash
Meows.exe --do
Meows.exe --do weighin.measure
```

With nothing after it, `--do` lists the jobs there are: `weighin.measure` takes Weigh-In's
reading, `nest.copy` copies what cannot be downloaded again, `cattery.read` asks git about every
repository. With a name it runs that one and prints what it did. The exit code is 0 done, 1 failed,
2 no such job, 3 the plugin is switched off. While Windows runs Weigh-In's reading, the tab's own
schedule stands down so the reading happens once, and anything worth knowing comes up as a
Windows notification.

## Building it yourself

```bash
git clone https://github.com/Zeralius/Meows.git
cd Meows
dotnet run --project Meows/Meows.csproj
```

You need the .NET 10 SDK. In Rider or Visual Studio, set **Meows** as the startup project and make
the run configuration build the **whole solution**: the shell does not reference the plugin
projects, so building only the startup project leaves you debugging yesterday's plugin DLLs.
[PLUGIN-GUIDE.md](PLUGIN-GUIDE.md#in-rider) covers that, along with some Rider XAML warnings that
are false positives.

```bash
dotnet test
```

The suite covers the filesystem and logic layers: duplicate scanning, disk measuring, queue
maths, file intake and its refusals, comic page ordering, clipboard conversion, cache
cataloguing, shortcut parsing, metadata stripping and post composition, and the plugin contract
rules. Every plugin's view is also built for real, headless, in both themes and both languages,
and anything Avalonia complains about fails the test, so a binding to a property that is not
there or a template that does not resolve is caught before it is a blank spot on screen.

## Writing a plugin

A plugin is a class library that references `Meows.Plugins.Abstractions` and exports one type:

```csharp
public sealed class MyPlugin : IMeowsPlugin
{
    public string Id => "meows.my-plugin";
    public string DisplayName => "My Plugin";
    public string Description => "What it does.";
    public string? Icon => "🎲";

    public Control CreateView(IMeowsHost host) =>
        new MyView { DataContext = new MyViewModel(host) };
}
```

`IMeowsHost` gives you a private data directory, JSON settings, the shared log, the notification
surface, background work the shell cancels for you, and the language the window is in.

Colours come from the shell too. Ask for `{DynamicResource MeowsCard}` rather than writing a hex
value and your plugin follows the theme; ship a `Strings.<code>.json` per language and it follows
the language. Both are looked up by name at run time, so neither needs a reference to the shell,
and a plugin that does neither still works.

There is a template that writes all of that for you:

```bash
dotnet new install Meows.Plugins.Template
dotnet new meows-plugin -n WeatherWatch
```

Drop the built DLL into a folder under `plugins/` and Meows picks it up.

Between the template and the plugins above sit the **[examples](examples/README.md)**: five
small plugins, one part of the contract each. A schedule and a condition with buttons; a
handoff out and handoffs in; the store, Ctrl+K on or off, and *Put back*; background work with
progress and Cancel and the save dialog; and the least a plugin can be. They are built and
smoke-tested with every release and never shipped, and a source build shows them on the
Plugins tab under **Examples**.

### Plugins from outside this repository

A plugin does not have to live here to be a Meows plugin. The contract is on NuGet as
[`Meows.Plugins.Abstractions`](https://www.nuget.org/packages/Meows.Plugins.Abstractions), the
template above is [`Meows.Plugins.Template`](https://www.nuget.org/packages/Meows.Plugins.Template),
and the shell loads whatever it finds in its plugins folder. So a plugin can be its own
repository, with its own version and its own releases, and nothing in this one has to change for
it to run. That is the intended way to build one that this repository has no reason to carry.

**Building one:** the two commands above, then

```bash
dotnet build -c Release -o WeatherWatch
```

The folder has to be named after the DLL. Zip that folder and it is the whole release. Set
`MEOWS_PLUGINS_DIR` to a folder of your own while you work, which adds to the search rather than
replacing it, so your plugin loads beside the built-in ones from wherever your build lands.

The template also writes `.github/workflows/release.yml`: push a tag like `v1.0.0` and GitHub
builds the plugin at that version, zips the folder and attaches it to a release, which is the
zip the button wants and the release the update check reads. `--Repository` on `dotnet new`
sets the `RepositoryUrl` the card links to and the check asks; `Authors` in the csproj is your
name.

**Installing one somebody else built:** open the **Plugins** tab, press **Install plugin…** and
pick the zip, or drop the zip on the tab. The plugin's card appears, switched off like every
other, with its version, author and repository under the description when the build stamped
them in, and **Uninstall** at the side, which asks once and then takes the folder away, leaving
the plugin's settings for a return. If it cannot be loaded, the card says why in place of its
toggle: most often that it was built for a newer contract than this Meows has, which an update
of Meows fixes. A plugin that throws is caught and marked **Failed**; it cannot take the app
down with it. Installing a plugin that is already there swaps the new version in, or, if the
old folder cannot be moved aside because it is in use, leaves the new one waiting until Meows
next starts; the notice under the buttons says which.

A plugin runs with the same rights as Meows. Install only what you trust.

**Staying current:** once a day, and once at start, Meows asks the GitHub repository of every
plugin installed this way for its latest release. A newer one shows on the card as *1.3.0 is
available* with an **Update** button; the download runs as a task and hands the zip to the
installer. Built-in plugins and folders copied by hand are never asked about, and a plugin whose
version or repository the build did not stamp in is left alone rather than guessed at.

By hand works too: **Open plugins folder**, unzip the whole folder into it so that
`plugins\WeatherWatch\WeatherWatch.dll` exists, and **Rescan plugins folder**. A folder that
arrived that way has no **Uninstall** and is not checked for updates: the shell only takes away
and replaces what it put there.

For a plugin that lives in this repository and ships with the release, **Kitten** writes it
instead: `dotnet run --project Meows.Kitten -- Whiskers --plain "Print queue"` makes the project,
adds it to the solution, the test project, the shipped list and the table above, then builds it
and runs the tests that know every plugin. See [Meows.Kitten/README.md](Meows.Kitten/README.md).

**[PLUGIN-GUIDE.md](PLUGIN-GUIDE.md)** is the full contract: project setup, every member of
`IMeowsHost`, notifications, background work, threading, lifetime, packaging and the assembly
isolation rules.

## How plugins are found

Each plugin lives in its own subfolder of a `plugins` directory. Meows looks for one:

1. in `MEOWS_PLUGINS_DIR`, if set, which takes a `;` separated list and adds to the search rather
   than replacing it
2. next to `Meows.exe`, which is the layout in the release zip
3. in any parent directory, which is what makes a source build find the repository's own folder

Inside a plugin folder, Meows prefers `<foldername>.dll` and otherwise scans every `.dll` there.
A folder ending in `.update` is a newer version waiting to replace its neighbour and is swapped in
before the next scan; `.installing` and `.old` are an install in progress and one just replaced,
and none of the three is scanned. Duplicate plugin ids are ignored, and a plugin that throws while starting up is caught, marked
**Failed** on its card and logged, so a broken plugin cannot take the app down with it.

## Repository layout

| | |
|---|---|
| `Meows/` | The shell: window, tab host, plugin loader. [README](Meows/README.md) |
| `Meows.Plugins.Abstractions/` | The contract a plugin implements, published as a NuGet package |
| `Meows.Plugins.*/` | The plugins listed above |
| `Meows.Bot.Core/` | Shared: the posting bot's config and media rules |
| `Meows.Disk/` | Shared: Recycle Bin deletion, folder walking, the drive scan, content hashing and what a folder is |
| `Meows.Media/` | Shared: metadata stripping and fitting a picture to a limit, on the shell's Skia |
| `template/` | The `dotnet new` template, published as `Meows.Plugins.Template` |
| `examples/` | Five small plugins, one part of the contract each; built and tested, never shipped. [README](examples/README.md) |
| `Meows.Kitten/` | Developer tool: writes a new in-tree plugin from a name and proves it builds. [README](Meows.Kitten/README.md) |
| `Meows.Tests/` | The test suite |

## Versioning

`major.minor.patch`, where **major** is a change to how the app works or looks, **minor** is a
new plugin or a substantial new capability inside one, and **patch** is a smaller feature or a
bug fix.

The app version is in `Meows/Meows.csproj` and shows at the bottom left of the window, so you can
always tell which build you are running.

`Meows.Plugins.Abstractions` carries its own version and does **not** move with the app, so adding
a plugin does not make every external plugin look out of date. Its version changes only when the
contract itself does: **major** if a member is removed or changed, **minor** if one is added,
**patch** for documentation. It reached **1.0.0** with Meows 3.0.0, which is the point from
which a plugin built against any 1.x loads on any later 1.x shell; plugins built against 0.x
are refused by a 3.x shell and need a rebuild against 1.0.0. **1.1.0**, with Meows 3.1.0,
added the line a plugin can put on its Home card. **1.2.0**, with Meows 4.2.0, added the two
lists a plugin gives the Rules tab, what it can be asked to do and what it records, and the
interface its view model does the asking through. **1.3.0**, with Meows 4.3.0, added the server
a plugin can copy a folder to. **1.4.0**, with Meows 4.4.0, added a plugin's Home line with no
window, for `--glance`. **1.5.0**, with Meows 4.17.0, added the jobs a plugin can do with no
window, for `--do`.

Meows checks that version when it loads a plugin and refuses anything it cannot honour, with the
reason on the plugin's card rather than a crash later:

> Built for Meows contract 1.6.0, which is newer than this shell's 1.5.0. Update Meows, or rebuild
> the plugin against 1.5.0.

A newer contract is refused; an older one is fine, since additions stay backward compatible.

The same check covers **Avalonia**, for the same reason. The shell and every plugin share one copy
of it, because a plugin hands back a `Control` and two copies mean two unrelated types with that
name. A plugin built against a different major, or a newer version than the shell carries, is
refused with the reason on its card rather than failing somewhere far from the cause.

## CI and releases

Both workflows run on `windows-latest`, which is required rather than preferred: the app is a
WinExe and several plugins delete through the Windows shell.

**[ci.yml](.github/workflows/ci.yml)** runs on every push to `main` and every pull request. It
builds in Release, runs the tests, and checks that the plugin contract still packs.

**[release.yml](.github/workflows/release.yml)** runs when a `v*` tag is pushed, or manually with
a version typed in. It runs the tests first, publishes the shell self contained for win-x64,
stages every plugin into `plugins/`, drops the third party native symbols, checks the package,
then zips it and attaches it to a GitHub release along with the contract `.nupkg`.

Staging the plugins is a separate step because the shell does not reference the plugin projects,
which is what keeps them drop in. A plain `dotnet publish` would produce an app with no plugins at
all. Which plugins to build and stage is worked out from the folders on disk rather than from a
list in the workflow, so adding one needs no edit here.

It also publishes `Meows.Plugins.Abstractions` and `Meows.Plugins.Template` to NuGet, using
[Trusted Publishing](https://learn.microsoft.com/en-us/nuget/nuget-org/trusted-publishing) rather
than a stored key: the job proves who it is with a token GitHub signs, and nuget.org returns a key
valid for one hour, so there is no long lived secret to leak. The policy on nuget.org names this
repository and `release.yml` by name, so renaming that file stops publishing until the policy is
updated to match. A fork with no `NUGET_USER` secret skips those steps and still gets a release.

The check before all that runs `Meows.exe --list-plugins` against the package and fails the build if
anything was refused or is missing a private library. Asking the app is not the same as checking
file names: a plugin whose shared library did not get staged loads perfectly and then fails the
moment it is switched on, which is exactly how three plugins once shipped broken.

To cut a release:

```bash
git tag v3.1.0 && git push origin v3.1.0
```

## Licence

MIT. See [LICENSE](LICENSE). Use it, fork it, build plugins on it and sell them if you like.
