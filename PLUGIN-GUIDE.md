# Writing a Meows plugin

Everything useful in Meows lives in a plugin. The shell finds them, lets you activate them, and
gives each one a tab. That is nearly all it does.

This guide covers the whole contract. For one part of it at a time, read the
**[examples](examples/README.md)**: five small plugins, one host feature each, built and
smoke-tested with every release. For the shape of a finished plugin, read
[Telegram Poster](Meows.Plugins.TelegramPoster/README.md),
[Purrge](Meows.Plugins.Purrge/README.md), [Kibble](Meows.Plugins.Kibble/README.md) or
[Chonk](Meows.Plugins.Chonk/README.md) alongside it.

---

## 1. The project

Copy an existing plugin's `.csproj`. Three things are load-bearing:

```xml
<PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <Nullable>enable</Nullable>
    <AvaloniaUseCompiledBindingsByDefault>true</AvaloniaUseCompiledBindingsByDefault>
    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>

    <!-- Debug drops straight into the folder the running shell scans, so F5 just works.
         Release goes elsewhere: sharing one path lets a Release build silently replace the
         plugin a Debug shell is loading. -->
    <OutputPath Condition="'$(Configuration)' == 'Debug'">$(MSBuildThisFileDirectory)..\plugins\My.Plugin\</OutputPath>
    <OutputPath Condition="'$(Configuration)' != 'Debug'">$(MSBuildThisFileDirectory)bin\$(Configuration)\</OutputPath>
</PropertyGroup>

<ItemGroup>
    <!-- The shell supplies both at runtime. Copying them here would invite a second,
         incompatible copy. -->
    <PackageReference Include="Avalonia" Version="12.1.1" ExcludeAssets="runtime" />
    <ProjectReference Include="..\Meows.Plugins.Abstractions\Meows.Plugins.Abstractions.csproj"
                      Private="false" ExcludeAssets="runtime" />
</ItemGroup>
```

Then `dotnet sln add` it. **The shell never references your project**, and that is what keeps
plugins drop-in. If you forget the `sln add`, a solution build silently skips you and you will
wonder why your changes do nothing.

---

## 2. Building and running

Needs the **.NET 10 SDK**. `dotnet --version` should report 10.x. Built and tested on 10.0.303.

### Day to day

```bash
dotnet build
```

```bash
dotnet run --project Meows/Meows.csproj
```

A Debug build drops each plugin straight into the repo's `plugins/` folder, which is exactly
where a Debug shell looks, so building is all it takes for your changes to be picked up.
Restart the app afterwards; plugins are not hot-reloaded.

Rebuilding just your own plugin is faster and enough, since the shell loads your DLL from disk
rather than referencing your project:

```bash
dotnet build Meows.Plugins.Purrge/Meows.Plugins.Purrge.csproj
```

### In Rider

Open `Meows.sln`, set **Meows** as the startup project, and run it.

**One trap will cost you an hour if nobody warns you.** A run configuration builds the startup
project and *its dependencies*. The shell deliberately does not reference plugin projects, which
is the whole basis of the drop-in design, so pressing Run **does not rebuild your plugin**. You
debug a stale DLL, your change appears to do nothing, and there is no error anywhere.

It is easy to confirm: edit a plugin source file, build only `Meows/Meows.csproj`, and the DLL in
`plugins/` keeps its old timestamp. Only `Meows.Plugins.Abstractions` and `Meows` are built.

Two ways round it, either is fine:

- **Build → Build Solution** before running, every time; or
- edit the run configuration so its *before launch* step is **Build Solution** instead of the
  default build of the startup project. Set it once and forget it.

The exact wording moves between Rider versions, but it is the before-launch list on the .NET
run configuration.

Other things worth knowing:

- **Breakpoints in plugin code work normally.** A Debug build copies the `.pdb` next to the
  plugin DLL, and although the assembly is loaded by reflection into its own
  `AssemblyLoadContext`, the debugger still resolves symbols from beside the DLL. *Attach to
  Process* works too if you started Meows outside the IDE.
- **Stop the app before building.** It holds its own exe and every plugin DLL, so a build
  fails with `MSB3027`. This bites more often in an IDE, where the app is easy to leave running.
- **`plugins/` and `artifacts/` are gitignored**, so the Solution view may hide them. Switch to
  the Files view when you want to see what a build actually produced.

Visual Studio behaves the same way: build the *solution*, not the startup project.

### XAML errors in Rider that are not real

Rider's Avalonia analyser flags ordinary bindings with:

> Invalid markup extension type: expected type is 'string?', actual type is 'CompiledBinding'

on `Text`, `IsVisible` and `IsChecked`, but not on `ItemsSource`, `Content` or `SelectedItem`.

Those are false positives. `dotnet build` compiles the same XAML with the Avalonia compiler and
reports zero errors, and the bindings work at runtime. The pattern gives it away: every flagged
property is typed `string`, `bool` or `bool?`, all types that cannot hold a reference, while the
unflagged ones are `object` or `IEnumerable`. The analyser is checking whether the markup
extension's *result* is assignable to the property, rather than applying the Avalonia rule that
an `IBinding` assigned to an `AvaloniaProperty` establishes a binding. It is a support gap for
Avalonia 12, not a problem with your code.

Update Rider first. If they persist, Alt+Enter on the error lets Rider write the correct
suppression into `.editorconfig` itself, which beats guessing the inspection id by hand.

### Developing a plugin in its own repository

You do not have to work inside the Meows solution at all. A plugin needs exactly two things from
Meows: the **contract**, and a way for the shell to **find** it. Both are available from outside,
and neither needs this repository cloned.

**1. Start from the template**, which writes a project that already does everything below:

```bash
dotnet new install Meows.Plugins.Template
dotnet new meows-plugin -n HelloMeows
```

**2. The contract is a package.** The generated `.csproj` references it from nuget.org, at the
contract version the template was published with. No `ProjectReference`, no Meows sources:

```xml
<ItemGroup>
    <PackageReference Include="Avalonia" Version="12.1.1" ExcludeAssets="runtime" />
    <PackageReference Include="Meows.Plugins.Abstractions" Version="1.2.0" ExcludeAssets="runtime" PrivateAssets="all" />
</ItemGroup>
```

`ExcludeAssets="runtime"` still matters, for the same reason as in-tree: the shell supplies both
at runtime, and a second copy of Avalonia in your output would make your `Control` a different
type from the one the shell can host. The Plugins tab says which contract version the Meows you
are running provides; pin that or anything older.

To build against a contract that is not on nuget.org yet, pack it from a checkout and point a
`nuget.config` beside your `.csproj` at the folder:

```bash
dotnet pack Meows.Plugins.Abstractions/Meows.Plugins.Abstractions.csproj -c Release -o artifacts/nuget
```

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="meows-local" value="F:\path	o\Meowsrtifacts
uget" />
  </packageSources>
</configuration>
```

**3. Tell the shell where your build lands.** `MEOWS_PLUGINS_DIR` takes a `;`-separated list and
is **additive**, so your plugin loads *alongside* the built-in ones rather than instead of them:

```bash
MEOWS_PLUGINS_DIR="C:\dev\HelloMeows\meows-plugins"
```

It still needs one subfolder per plugin, named after the DLL, which is what
`dotnet build -c Release -o C:\dev\HelloMeows\meows-plugins\HelloMeows` gives you. The log then
shows both roots being scanned:

```
[plugins] Scanning C:\dev\HelloMeows\meows-plugins
[plugins] Found 'Hello' (external.hello).
[plugins] Scanning F:\...\Meows\plugins
[plugins] Found 'Purrge' (meows.purrge).
```

Everything else behaves identically: your own `%APPDATA%\Meows\plugins\<your-id>\` data
directory, notifications, background work.

**4. Handing it to someone.** The build folder, zipped, is the release:

```bash
dotnet build -c Release -o HelloMeows
```

Zip the whole folder, not just the DLL: a plugin's own libraries are loaded from beside it. The
person receiving it presses **Install plugin…** on the Plugins tab, picks the zip or drops it on
the tab, and the card appears, switched off like every other. The installer names the folder
after the DLL, so it does not matter what the zip or the folder inside it was called, and it
refuses a zip in which it cannot tell which DLL is the plugin, which does not happen to a build
folder because the `.deps.json` says. A plugin that is already installed is already loaded, so a
new version is put beside it as `HelloMeows.update` and the old folder is renamed aside to make
room; when that rename is refused because the folder is held open, the new one waits until
Meows next starts, and the notice under the buttons says which happened.

The template does the zipping for you: `.github/workflows/release.yml` builds on a `v*` tag with
the tag's number as the version, zips the folder and attaches it to a GitHub release.

**5. What the card says, and the update check.** Meows reads three things the SDK stamps into
the DLL and shows them under the description: `Version` (as `AssemblyInformationalVersion`, with
any `+commit` suffix cut off), `Authors` (as `AssemblyCompany`, hidden when it is just the
assembly name, which is the SDK's default), and `RepositoryUrl` (as an `AssemblyMetadata`
attribute, shown as a link). None of that is contract; a plugin that sets none of them gets a
plainer card. The template sets all three, `--Repository` on `dotnet new` fills the last.

Once a day, and once at start, the shell asks the GitHub repository of every plugin the installer
put there for its latest release, one request each, unauthenticated. A tag that parses as a
higher version than the DLL's, with a `.zip` among the release's assets, shows on the card as
*available* with an **Update** button; the download runs as a task and goes through the same
installer. So a plugin takes part by doing nothing more than setting `RepositoryUrl`, tagging
releases `vMAJOR.MINOR.PATCH`, and attaching the zip, which is what the workflow does. A plugin
without a numeric version, or without a GitHub `RepositoryUrl`, is never asked about.

#### Version compatibility

`Meows.Plugins.Abstractions` is deliberately **shared** with the shell rather than loaded from
your folder, and that is what makes type identity work across the load context boundary. The
consequence: at runtime your plugin uses the shell's copy, not the one you compiled against.

The shell checks this for you. At discovery it reads the contract version your assembly was
compiled against and refuses anything it cannot honour, **before constructing your plugin**, so
none of your code runs. The reason appears on your plugin's card in place of its toggle:

> Built for Meows contract 1.3.0, which is newer than this shell's 1.2.0. Update Meows, or rebuild
> the plugin against 1.2.0.

A mismatched **major** is refused either way, since a major bump means members may have been
removed. A **newer** minor or patch is refused; an older one loads fine, because additive
changes stay backward compatible. The Plugins tab shows the contract version the shell provides,
and [README.md](README.md#versioning) covers when each part gets bumped.

Without this you would get a `MissingMethodException` from somewhere unhelpful at activation
time, and no build error to warn you. The runtime binds the assemblies happily and only trips
when a missing member is actually called.

### A distributable build

The shell does not reference plugin projects, which is what makes them drop-in, so
`dotnet publish` on its own produces an app **with no plugins at all**. Staging them is a
separate step, and the easiest one to forget.

```bash
dotnet publish Meows/Meows.csproj -c Release -r win-x64 --self-contained true -o artifacts/Meows-win-x64
```

Then build each plugin in Release. Release output goes to `bin/`, deliberately *not* to the dev
`plugins/` folder, so this cannot disturb a Debug shell you have running:

```bash
for p in Meows.Plugins.TelegramPoster Meows.Plugins.Purrge Meows.Plugins.Kibble Meows.Plugins.Chonk; do dotnet build "$p/$p.csproj" -c Release; done
```

Stage each one into the folder the deployed shell scans. Copy the **whole** build output rather
than just the plugin DLL. A plugin's own libraries are loaded from its folder, so a plugin with
any dependency fails at activation if you cherry-pick, and it fails long after the build looked
fine. Telegram Poster, Kibble, Perch and Portion need `Meows.Bot.Core.dll` this way; Purrge,
Chonk, Scruff and Portion need `Meows.Disk.dll`, as does `Meows.Bot.Core` itself; and Scruff and
Portion need `Meows.Media.dll`:

```bash
for p in Meows.Plugins.TelegramPoster Meows.Plugins.Purrge Meows.Plugins.Kibble Meows.Plugins.Chonk; do mkdir -p "artifacts/Meows-win-x64/plugins/$p" && cp "$p"/bin/Release/*.dll "$p"/bin/Release/*.deps.json "artifacts/Meows-win-x64/plugins/$p/"; done
```

Avalonia and `Meows.Plugins.Abstractions` are not in that output to be copied, because the csproj
files keep them out. That is on purpose: the shell has to be the only source of both. The same
trick works for anything else the shell already carries: `Meows.Media` draws with SkiaSharp,
which Avalonia loads anyway, so it references the package with `ExcludeAssets="runtime;native"`
at the exact version Avalonia brings and picks up the shell's copy at run time. A name that is not on the
shared list still resolves from the shell when the plugin folder has nothing by that name.

Finally drop the third-party native symbols. This matters more than it sounds. `libSkiaSharp.pdb`
and `libHarfBuzzSharp.pdb` are about 100 MB of a 206 MB output. Meows' own PDBs stay, so stack
traces remain readable:

```bash
find artifacts/Meows-win-x64 -maxdepth 1 -name "*.pdb" ! -name "Meows*.pdb" -delete
```

That leaves roughly 106 MB, or 45 MB zipped.

**Not trimmed and not single-file**, both on purpose. Trimming breaks Avalonia's XAML
reflection, and single-file changes what `AppContext.BaseDirectory` resolves to, which is
precisely what plugin discovery depends on.

### Testing the deployed layout

Run a published build from **outside** the repository. In place, the ancestor probe finds the
repo's own `plugins/` folder and a completely broken deployment still looks fine:

```bash
cp -r artifacts/Meows-win-x64 "$TEMP/meows-test" && "$TEMP/meows-test/Meows.exe"
```

`%APPDATA%\Meows\meows.log` should name the deployed folder, not the repo one. Watch the working
directory too: launching from the repo lets a plugin's own probing find repo paths it would
never see on a real machine.

### When the build fails

| Symptom | Cause |
|---|---|
| `MSB3027 ... locked by "Meows"` | The app is running. Close it, since it holds its own exe and every plugin DLL |
| Your plugin never appears | Not in the solution, so a solution build silently skips it. `dotnet sln add` it |
| Changes have no effect | You built Release, which goes to `bin/` rather than the dev `plugins/` folder |
| Plugin loads but the tab is empty | Check `meows.log`; an exception in `CreateView` is caught and marked *Failed* |

---

## Starting from the template

```bash
dotnet new install Meows.Plugins.Template
dotnet new meows-plugin -n WeatherWatch --Category "Everyday"
```

That writes the project, the plugin class, a view and a view model, already wired together. The id
comes from the name in lower case; change it before anyone installs the plugin, because it is the
settings key and moving it later orphans whatever was stored.

Everything below is what the template writes and why, which is worth reading once even if you
never write it by hand.

## Starting inside the repository: Kitten

The template is for a plugin that lives outside this repository and drops into a `plugins`
folder. A plugin that lives *here* and ships with the release has more to be: on the solution,
referenced by the test project, on the shipped list every plugin-wide test walks, in the README
table, with a version bump and a changelog line. **Kitten** writes all of it from a name:

```bash
dotnet run --project Meows.Kitten -- Whiskers --plain "Print queue" --icon 🧵 --category group.disk
```

It writes the project in the house style, view model with the header state, error strip,
language watch, `ISearchable` hook and dispose, both string catalogues, a README and a test
file, makes the six edits, then builds the plugin, builds the tests and runs every test that
knows every plugin, so a template that has drifted from the shell fails in the terminal rather
than at release time. `--dry-run` says what would happen; `--no-verify` skips the build. See
[Meows.Kitten/README.md](Meows.Kitten/README.md).

## 3. The entry point

```csharp
public sealed class MyPlugin : IMeowsPlugin
{
    public string Id => "meows.my-plugin";   // stable forever: it is the settings key
    public string DisplayName => "My Plugin";
    public string PlainName => "myplugin.name.plain";   // what it is, in plain words; optional
    public string Description => "One sentence, shown on the Plugins tab.";
    public string? Icon => "🎲";            // shown on the tab header
    public string? Category => "Everyday";  // heading on the Plugins tab, optional

    public Control CreateView(IMeowsHost host) =>
        new MyView { DataContext = new MyViewModel(host) };
}
```

Public, with a parameterless constructor, since the shell instantiates it by reflection. `Id` is
the identity for stored settings and activation state, so changing it later orphans both.

`PlainName` is what the plugin is when the feline name is switched off on the Settings tab:
Purrge is Duplicates, Chonk is Disk usage. Return a key from your catalogue so it is translated.
It is optional; a plugin that does not say reads the same in both modes. The feline `DisplayName`
stays the identity underneath, in the log and as the source of notifications and tasks.

`Category` is optional and has a default, so leaving it out compiles and loads exactly as before;
the plugin simply appears under **Everything else**. The shell does not interpret the text and
holds no list of valid groups: two plugins share a heading when they spell it the same way,
ignoring case. Pick an existing one to join it, or invent your own.

`CreateView` runs once per activation. If it throws, the shell catches it, marks the plugin
*Failed* on its card, and logs the exception; a broken plugin cannot take the window down.

### Being searched while switched off

Ctrl+K asks every open plugin's view model through `ISearchable` ([section 4](#being-searched-from-ctrlk)).
A plugin that is off has no view model, so from 1.0.0 it can be asked through the plugin class
instead:

```csharp
public ISearchable? WhileOff(IMeowsDormantHost host)
{
    var settings = host.LoadSettings<MySettings>();
    return settings is null || settings.Things.Count == 0 ? null : new Asleep(host, settings.Things);
}
```

`IMeowsDormantHost` is the little a plugin gets while off: `PluginId`, `DataDirectory`,
`LoadSettings`, `Text`, a read-only view of its `Store`, and `Handoff`. No log, no
notifications, no background work: a plugin that is off is off. Return null to be left out
until switched on, which is the default and what a plugin that does not override this gets.

The shell asks once, the first time the palette needs it after a scan, and keeps the answer
until the plugin is switched on or the list is read again, so read what you keep in
`WhileOff` and answer `Search` from memory. A hit's `Open` cannot reach a view model that does
not exist yet; the way in is to hand the plugin to itself:

```csharp
new SearchHit(thing.Title, thing.Kind, () =>
    host.Handoff.Send(host.PluginId, new Handoff("myplugin.show", [], thing.Id)))
```

The shell answers a handoff to a plugin that is off by switching it on, bringing its tab to the
front and delivering, so the view model's `IHandoffTarget.Receive` gets the verb with the id in
`Note` and selects the thing. Collar does exactly this for its dates: `CollarPlugin.WhileOff`
and the `collar.show` verb in `CollarViewModel.Receive` are the worked example. Kibble answers
with the files waiting in its last folder, one listing when first asked, and Familiar with its
one-shots by name; a plugin whose data only exists once it is on, Tin's accounts or Birdwatch's
posts, answers nothing and is left out, which is the honest default.

### Being asked by a rule

The shell's **Rules** tab joins plugins up: *when* one records an event, *then* ask another to
do something. Since 1.2.0 a plugin takes part through two lists on the plugin class and one
interface on its view model. Both lists are read while the plugin is off, so they are fixed
lists, never read from settings or the disk.

```csharp
public IReadOnlyList<PluginAction> Actions =>
[
    new("check", "myplugin.action.check", "myplugin.action.check.hint"),
];

public IReadOnlyList<RecordedKind> Records =>
[
    new("found", "myplugin.records.found"),    // the kind you pass to Store.Record
];
```

`Actions` is what a rule can ask for: a stable id, which is what rules are saved by, and a label
and an optional sentence, both keys from your catalogue. `Records` names the kinds of event you
already write to the store, with a past-tense phrase that follows your plugin's name on the
Rules tab, "Birdwatch *saved a picture*", so a rule can wait for one before it has ever happened.
A kind you record but do not list can still start a rule once it is in the history; it is shown
by its bare word. Both default to empty, and a plugin with no actions is simply not on the
"then" side.

The asking goes to the view model:

```csharp
public sealed class MyViewModel : ObservableObject, IActionTarget
{
    public async Task<string> Perform(ActionRequest request, CancellationToken token)
    {
        if (request.Action != "check")
            throw new ActionDeclinedException($"My plugin does not know how to {request.Action}.");
        if (!File.Exists(request.Path))
            throw new ActionDeclinedException($"{request.Path} is not there any more.");

        var found = await CheckAsync(request.Path, token);
        return $"{found} things found";              // the line History shows
    }
}
```

The shell switches the plugin on if it is off, without bringing its tab to the front, and calls
`Perform` on the UI thread. Do the work the way the tab would, through background work if it
is long, and return one sentence saying how it went. `request.Cause` is the whole event that set
the rule off; `request.Path` is where the thing is now, which is the cause's subject unless the
recording plugin moved it and wrote the new place as `destination` in its data, as Kibble does.
Throw `ActionDeclinedException` for "not this time", the file has gone or the tab is busy, and
anything else for a real failure; History tells the two apart. The token is cancelled when
Meows quits, when your plugin is switched off, and when an action has run for an hour.

Whatever you record while performing is marked as the rule's doing and never starts another
rule. That is the one-hop rule, and it is kept by the shell, not by you: the store marks every
line written from inside `Perform`, including from work it awaited on another thread, and
treats any line from a plugin that is busy with a rule the same way. Collar, Portion, Purrge,
Scruff and Weigh-In are the worked examples; Collar's is the smallest, and Weigh-In's shows
several rules asking at once sharing one pass.

---

## 4. `IMeowsHost`

Your whole view of the shell. One instance per plugin, scoped to you.

```csharp
public interface IMeowsHost
{
    string PluginId { get; }
    string DataDirectory { get; }
    void Log(string message);
    void Log(LogLevel level, string message); // 0.9.0
    T? LoadSettings<T>() where T : class;
    void SaveSettings<T>(T settings) where T : class;
    IMeowsNotifications Notifications { get; }
    IMeowsBackgroundWork Background { get; }
    IMeowsText Text { get; }
    IMeowsSecrets Secrets { get; }      // 0.5.0
    IMeowsHandoff Handoff { get; }      // 0.5.0
    IMeowsStore Store { get; }          // 0.5.0
    IMeowsWatches Watches { get; }      // 0.7.0
    IMeowsPicker Pick { get; }          // 1.0.0
}
```

`Notifications` grew its many-button overloads in 0.8.0; see [section 6](#6-notifications).

The last five have defaults, so a plugin built against an older release of the same major
still compiles and loads; the defaults hand back the key, hold no secrets, reach no other
plugin, and pick nothing.

**Contract 1.0.0** is where the 0.x run ended and the promise started: within a major, a member
is only ever added, never removed or changed, and the shell loads anything built against an
older minor. A plugin built against 0.x is refused by a 1.x shell, before any of its code runs,
with *rebuild against 1.0.0* on its card. The members that came with 1.0.0 are `Pick` below and
`IMeowsPlugin.WhileOff` in [section 3](#3-the-entry-point); nothing was taken away. **1.1.0**
added `IGlanceable`, [a line on the Home tab](#a-line-on-the-home-tab), and nothing else.
**1.2.0** added `IMeowsPlugin.Actions` and `IMeowsPlugin.Records`, `IActionTarget` and
`ActionDeclinedException`, for [being asked by a rule](#being-asked-by-a-rule).

### `DataDirectory`

A writable folder at `%APPDATA%\Meows\plugins\<your-id>\`, created before you see it. Put caches
and databases here. Never write inside the repository or next to the executable.

### `Log`

Goes to the shared log pane, the Log tab and `%APPDATA%\Meows\meows.log`. Safe from any thread.
Use it for a trail you would want when something misbehaves, not for anything the user must act
on. That is what notifications are for.

`Log(LogLevel.Warning, ...)` and `Log(LogLevel.Error, ...)` (0.9.0) mark a line as trouble: the
Log tab colours it, counts it, and can be set to show *only* trouble, and a person who has turned
your plugin down to *Warnings and errors* on that tab still sees it. Plain `Log(...)` is Info.
The file keeps every line whatever the tab is set to; the levels decide what is shown, which is
what "quiet" means for a plugin with a lot to say. Say what failed at Warning, what went wrong in
a way that needs looking at at Error, and everything else at Info.

### Settings

Plain JSON round-trip into `DataDirectory`, camelCase, no schema:

```csharp
public sealed class MySettings
{
    public string? LastFolder { get; set; }
    public int BatchSize { get; set; } = 50;
}

_settings = host.LoadSettings<MySettings>() ?? new MySettings();
_settings.LastFolder = picked;
host.SaveSettings(_settings);
```

Returns `null` when nothing was saved yet, **and also when the file is unreadable**, so always
`?? new()` rather than assuming null means first run.

Never store secrets here. Tokens and passwords belong wherever the tool they belong to keeps
them; the Telegram plugin writes `BOT_TOKEN` to the bot's own `.env` and only ever reports
*whether* one exists. When the credential has nowhere else to live, use `Secrets`, below.

### `Secrets`

For an app password or an access token that your plugin itself has to hold. Each one is a file
under `secrets\` in your data folder, sealed with Windows data protection for the current user, so
it opens on this machine for this account and is noise anywhere else.

```csharp
host.Secrets.Set("bluesky", json);
var json = host.Secrets.Get("bluesky");   // null if absent, or unreadable
host.Secrets.Forget("bluesky");
```

Prefer something revocable: an app password over the account password, a token over a login.
Show the account name on your card, which is not a secret, and keep the credential itself off
the screen once it is saved. Scruff is the worked example.

### `Handoff`

One plugin handing work to another. Chonk finds a fat folder and offers *Find duplicates in it*;
pressing it opens Purrge, if it is installed, brings its tab to the front and starts the scan
there. Kibble's right-click offers *Clean with Scruff* the same way.

```csharp
// The sender. CanReach says whether the plugin is installed; Send says whether it took it.
if (host.Handoff.CanReach(KnownPlugins.Purrge))
    host.Handoff.Send(KnownPlugins.Purrge, Handoff.Folder(path));
```

```csharp
// The receiver: the view model implements IHandoffTarget. Accepts is asked first, so a sender
// is told no rather than having its handoff dropped.
public bool Accepts(Handoff handoff) =>
    handoff.Verb == HandoffVerbs.Folder && handoff.Paths.Count == 1;

public void Receive(Handoff handoff) => LoadFolder(handoff.Paths[0]);
```

**An answer, when the sender wants one** (0.9.0). A sender that would like to know how it went
sets `Reply` on the handoff; the receiver calls `handoff.Answer("3 sets, 12 copies")` once, when
the work the handoff asked for is done, which may be after a scan rather than in `Receive`. The
shell puts the reply on the UI thread, runs it once and logs it, so the sender's status line can
take it directly:

```csharp
// The sender
_host.Handoff.Send(KnownPlugins.Purrge, Handoff.Folder(path) with { Reply = outcome => Status = outcome });

// The receiver, when its scan lands
_askedBy?.Answer(ResultSummary);
```

`Answer` on a handoff nobody asked about is a quiet no-op, so a receiver can always call it.

Two verbs are agreed on, `HandoffVerbs.Folder` and `HandoffVerbs.Files`; two plugins may invent a
third between themselves. The receiver is switched on if it is installed but off, because "open
this in Purrge" means open Purrge. `Receive` is called on the UI thread after the tab has come to
the front. Hide the button when `CanReach` is false, so a plugin that is not there is not offered.

### `Store`

The shared store: one SQLite file under `%APPDATA%\Meows`, owned and versioned by the shell. A
plugin gets three things through it.

**A journal.** `Record(kind, subject, detail, data)` writes one line: a short kind you will filter
on (`"sent"`, `"recycled"`, `"posted"`), the path or name it happened to, a sentence for a person,
and a small dictionary for code. Kibble writes one per file it queues, Purrge per file it recycles,
Scruff per post, Portion per shrink, Collar per date dealt with. The shell's **History** tab shows
every plugin's lines together; `Recent` and `Search` show you your own.

**A notebook.** `Get`, `Set`, `Remove` for small durable facts scoped to your plugin: things
learned rather than chosen, which is what separates them from settings.

**One shared table.** `MarkSeen(hash)` and `Seen(hash)` say whether any plugin has seen this
content before, by hash. Shared on purpose: a picture Birdwatch saved and a picture Kibble queued
are the same picture if the bytes agree. First sighting wins.

It is a journal and a notebook, not a database to design tables in. A plugin that needs its own
tables keeps its own file in `DataDirectory`.

### Being searched from Ctrl+K

The command palette finds tabs, settings and history lines by itself. To let it reach into what
your tab is showing, have your view model implement `ISearchable` (0.6.0):

```csharp
public IReadOnlyList<SearchHit> Search(string query, int limit)
{
    var words = SearchWords.Split(query);
    return Items
        .Where(i => SearchWords.Match(words, i.Name, i.Folder))
        .Take(limit)
        .Select(i => new SearchHit(i.Name, i.Folder, () => Selected = i))
        .ToList();
}
```

A hit is a title, a line of detail and what to do when Enter lands on it. The shell brings your
tab to the front first, so `Open` only has to select or reveal the thing. It is called on the UI
thread on every keystroke while the palette is open: answer from what is already in memory, never
from the disk or the network, and use `SearchWords.Match` so every plugin answers to the same
rule the palette ranks by. Only plugins that are switched on are asked, and a hit must never do
anything but show: Kibble deliberately does not offer its destinations, because the one thing to
do with a destination is send.

### A line on the Home tab

Home is tab zero, and every switched-on plugin has a card there with what the shell can work
out by itself: the last journal entry and the state of its watches. A view model that
implements `IGlanceable` (1.1.0) puts its own line above that:

```csharp
public Glance? Glance() => Summary.Length == 0 ? null : new Glance(Summary, Entries.Any(e => e.IsOverdue));
```

One sentence, what the plugin would say if asked "anything?": Collar's "1 have passed, 2 more
coming up", Portion's "2 would fail to post, 1 of them shrinkable", Purr's "9 watches across 6
plugins 1 stopped".
`IsTrouble` paints it red, which is for something that wants doing, not for something that
merely happened. Null means nothing worth a line right now, and the card falls back to the
shell's words; Portion answers null before its first scan, because "press Scan" is not news.

It is read on the UI thread whenever Home comes to the front or is refreshed, so answer from
what is in memory, the way `Search` does. Most plugins already have the sentence: it is the
header line of the tab. A plugin that is off has no view model and no line; that is what the
shell's own line is for.

### `Watches`

Every schedule every plugin has running, deliberately not scoped, with the times the shell
records as passes finish and the reason a schedule stopped. Purr is the one plugin that reads
it; you will not usually need to. `Changed` fires on the UI thread whenever a watch starts,
passes, fails or ends.

**Pause and resume** (0.10.0). Each `WatchInfo` carries an `Id`; `Pause(id, until)` holds that
schedule (`DateTime.MaxValue` for "until I say") and `Resume(id)` lets it look again at once.
The plugin that owns the schedule is not told; its passes simply do not run, and `PausedUntil`
on the info says so. Both return false on a shell older than 0.10.0 or for a watch that has
stopped. Purr puts three buttons on it; nothing else needs to.

### `Pick`

The shell's file dialogs, so a view model can ask for a file without a `TopLevel` in hand:

```csharp
var path = await _host.Pick.File(new PickOptions
{
    Title = _host.Text["myplugin.pick.receipt"],
    Filters = [PickFilter.Of("Pictures", "*.jpg", "*.png"), PickFilter.Of("PDF", "*.pdf")],
});
if (path is null) return;   // cancelled, or no window to ask over
```

`File`, `Files`, `Folder`, `Folders` and `Save`, each taking the same optional `PickOptions`:
title, filters, where to start, and for `Save` a suggested name and a default extension. Every
one answers null, or an empty list, when the user cancelled and also when there is no window
showing, which is what a plugin driven from the tray gets and what a test's fake host answers.
The dialogs are put over the main window; a tab in a window of its own still gets them there.

Every built-in plugin picks this way, from a command on its view model, and none of their
code-behinds opens a dialog any more; `ChonkViewModel.PickFolderCommand` is the shape. In the
tests, `FakeHost.Picks` answers each dialog with the next scripted path, so the whole flow runs
without a window. Reaching for `TopLevel.GetTopLevel(this)?.StorageProvider` in code-behind
still works, if a plugin would rather.

### `Explorer`

Not on the host, since it needs nothing from the shell, but in the same assembly:
`Explorer.Open(target)` opens a file, folder or URL the way Windows would, `Explorer.Reveal(path)`
opens Explorer with the file selected, and `Explorer.OpenWith(path)` shows the *Open with* dialog.
Each throws on failure the way `Process.Start` does, so wrap it in the try/catch that sets your
error line.

### `Text`

The language the window is in. See [section 5](#5-colours-and-language) for how to use it.

---

## 5. Colours and language

The shell hands both of these out by name at run time, so a plugin picks them up without
referencing anything but the contract. A plugin that ignores both still works; it simply stays
in English and paints its own colours in every theme.

### Colours

The window is light or dark depending on what the person chose on the Settings tab, and Windows
gets a vote when they chose to follow it. A hardcoded `#1C1C22` looks right in one of those and
wrong in the other, so ask for a token instead:

```xml
<Border Background="{DynamicResource MeowsCard}"
        BorderBrush="{DynamicResource MeowsLine}"
        BorderThickness="1"
        CornerRadius="8" />
```

Lookup walks up to the application, so this resolves from the shell without a reference, and it
follows a theme change on its own.

| Token | For |
|---|---|
| `MeowsSunken` | the recess a thumbnail or a code block sits in |
| `MeowsPanel` | a drawer along an edge |
| `MeowsBase` | the background of a whole pane |
| `MeowsBar` | the strip along the bottom |
| `MeowsCard` | a card or a row |
| `MeowsInset` | a well inside a card |
| `MeowsRaised` | something standing slightly proud |
| `MeowsHeader` | a column heading |
| `MeowsLine` / `MeowsLineSoft` | separators, the second one subtler |
| `MeowsSelection` / `MeowsSelectionLine` | the selected row |
| `MeowsInfo` / `MeowsInfoLine` / `MeowsInfoText` | saying something, nothing wrong |
| `MeowsWarn` / `MeowsWarnLine` / `MeowsWarnText` | worth reading before carrying on |
| `MeowsDanger` / `MeowsDangerLine` / `MeowsDangerText` / `MeowsDangerStrong` | something is wrong |
| `MeowsGoodText` | healthy |
| `MeowsBadge` / `MeowsDangerBadge` | a badge over a thumbnail, carrying its own opacity |
| `MeowsVeil` | dims the window behind a confirmation |

Text and controls come from the Fluent theme and follow the variant without you doing anything,
so leave `Foreground` alone unless you mean something by it.

### Language

Ship a `Strings.<code>.json` per language as an embedded resource and the shell merges it into one
table when it finds your plugin. Keys are flat and namespaced by you:

```json
{
  "weatherwatch.title": "Weather Watch",
  "weatherwatch.found": "{0} stations within {1} km"
}
```

```xml
<ItemGroup>
    <EmbeddedResource Include="Strings\Strings.*.json">
        <WithCulture>false</WithCulture>
        <LogicalName>$(AssemblyName).Strings.%(Filename)%(Extension)</LogicalName>
    </EmbeddedResource>
</ItemGroup>
```

Both of those lines matter. MSBuild reads the middle of `Strings.de.json` as a culture and, left
alone, files it under a `de` satellite assembly and strips the code out of the name, so both
languages end up called `Strings.json` and neither is where the shell looks.

From XAML, with `xmlns:m="using:Meows.Plugins.Abstractions"`:

```xml
<TextBlock Text="{m:Tr weatherwatch.title}" />
```

That is a binding, not a lookup, so it repaints when the language changes rather than needing a
restart. From code, through the host:

```csharp
Status = host.Text.Format("weatherwatch.found", count, radius);
```

### The half that is easy to miss

`{m:Tr}` looks after itself, because it is a binding to a string that announces when it changed.
**Anything your view model works out in code does not.** The property would return the new
language perfectly well, but nothing asks it to, so an open tab keeps showing whatever it read
when it opened. Half of Mouser stayed in German this way and it looked like the setting had not
taken.

Hold a `LanguageWatch` for the life of the view model:

```csharp
private readonly LanguageWatch _language;

public MyViewModel(IMeowsHost host)
{
    ...
    _language = new LanguageWatch(OnEverythingChanged);
}

public void Dispose() => _language.Dispose();
```

`OnEverythingChanged` is on `ObservableObject` and says every property may now read differently,
which is exactly what a language change is. It saves you listing your own text properties, which
is a list that goes stale the first time you add one.

Disposing it matters. The string table outlives every plugin, so a view model that forgets stays
alive through it, along with the whole tab hanging off it.

**Do not capture translated text into a field**, either:

```csharp
private string _status = host.Text["my.status.start"];       // stuck in whatever language
                                                             // it was switched on in

private string? _status;                                     // better
public string Status
{
    get => _status ?? MeowsText.Current["my.status.start"];
    private set => SetField(ref _status, value);
}
```

Null meaning "nothing has happened yet" lets the opening line follow the language, while a
message about something that already happened stays as it was said. Re-translating that would be
rewriting history, and impossible anyway once numbers are formatted into it.

Three things are worth knowing:

- A key nobody has is shown as the key. Visibly wrong, never an exception from inside a binding.
- A key English has and your language does not reads in English. Half a window in the wrong
  language is a nuisance; half a window of dotted identifiers is unusable.
- `Description` and `Category` on `IMeowsPlugin` go through the table too, so returning a key
  gets them translated and returning a sentence shows it as written.

The shell ships `en` and `de`. Log messages are deliberately not translated: the log is what gets
pasted into a bug report.

---

## 6. Notifications

Use these when the user needs to know something. The point is that the shell owns one surface,
so a problem raised by a tab in the background is still seen.

```csharp
public interface IMeowsNotifications
{
    void Post(NotificationSeverity severity, string title, string message = "",
        NotificationAction? action = null);

    void SetCondition(string key, NotificationSeverity severity, string title,
        string message = "", NotificationAction? action = null);

    // 0.8.0: as many buttons as the event deserves
    void Post(NotificationSeverity severity, string title, string message,
        params NotificationAction[] actions);
    void SetCondition(string key, NotificationSeverity severity, string title,
        string message, params NotificationAction[] actions);

    void ClearCondition(string key);
}
```

**`Post` is for events.** Something finished, something failed. The user can dismiss it.

```csharp
host.Notifications.Post(NotificationSeverity.Info, "Import finished", "42 files added");
```

**`SetCondition` is for states.** Something is wrong *right now* and stays wrong until fixed.
Posting the same key again replaces the entry rather than stacking, so a re-check on a timer
cannot pile up duplicates. The user cannot dismiss a condition. Only you can clear it, because
only you know whether it still holds:

```csharp
if (!python.Found)
    host.Notifications.SetCondition("missing-tools", NotificationSeverity.Error,
        "Python not found",
        "The bot cannot start without it.",
        new NotificationAction("Re-check", CheckTools));
else
    host.Notifications.ClearCondition("missing-tools");
```

Both branches matter. A condition you set and never clear is worse than no condition at all.

Keys are scoped to your plugin, so `"missing-tools"` cannot collide with another plugin's.
Everything you raised is retracted automatically when you are deactivated.

`NotificationAction.Invoke` is called on the UI thread, and the shell catches anything it
throws.

**Buttons that do the thing.** A notification can carry two or three actions, each its own
verb, since 0.8.0: Collar's one due date has *Done*, *Not this week* and *Look again*; Portion's
warning has *Shrink what can be shrunk*; a failed schedule's error has *Restart the plugin*, put
there by the shell. `DismissesAfter: true` on an action takes an event notification down once
the button has done its work, which is what a button on a toast usually means; a condition is
never dismissed that way, your own action clears it if it no longer applies. More than three
buttons is a list, and a list belongs on your tab. A shell from before 0.8.0 shows the first
button only, through the defaults on the interface.

### Notification or in-tab banner?

| | |
|---|---|
| **Notification** | Environmental or time-shifted: a missing tool, a crashed process, a finished import. True whether or not your tab is open |
| **In-tab** | Contextual to what is on screen: which group is misconfigured, which file is selected |

Purrge posts a notification when a scan finishes, because scans are long and you will be
elsewhere. It draws duplicate sets in the tab, because they only mean anything there.

---

## 7. Background work

For anything that should keep running while the user is on another tab: a folder watch, a long
import, a periodic scan.

```csharp
public interface IMeowsBackgroundWork
{
    IBackgroundTask Run(string title, Func<IBackgroundContext, Task> work);

    IBackgroundTask Schedule(string title, TimeSpan interval, Func<IBackgroundContext, Task> work,
        bool runImmediately = true);
}
```

```csharp
_scan = host.Background.Run($"Scanning {folder}", async context =>
{
    context.Report("Listing files…");
    context.ReportProgress(null);          // null = indeterminate

    var found = await _scanner.ScanAsync(folder, options, progress, context.Token);

    await Dispatcher.UIThread.InvokeAsync(() => ApplyResults(found));
});
```

The shell owns the lifetime. Everything registered here is cancelled when your plugin is
deactivated or the app closes, so you cannot leak a loop into the background. Faults are caught,
logged, and turned into an error notification rather than becoming an unobserved task exception.

`Schedule` waits `interval` **between** passes rather than on a fixed clock, so a slow pass
delays the next one instead of two overlapping.

### Rules

1. **Honour `context.Token`.** Pass it to every async call and check it in loops. Ignoring it
   means deactivation cannot stop you.
2. **Never touch UI state directly.** Background work runs on a thread pool thread. Marshal
   with `Dispatcher.UIThread.InvokeAsync` before touching an `ObservableCollection` or anything
   bound. `Report` and `ReportProgress` are the exception, since they marshal for you.
3. **`Report` a status a human would understand.** It is displayed verbatim in the task panel.
   "Comparing content… 12 sets so far" beats "phase 2".
4. **Let `OperationCanceledException` propagate.** The shell treats it as a clean stop and says
   nothing; swallowing it makes cancellation look like success.

---

## 8. MVVM and threading

`ObservableObject` and `RelayCommand` ship in the contract assembly, so a plugin needs no MVVM
dependency:

```csharp
public sealed class MyViewModel : ObservableObject, IDisposable
{
    private string _status = "";

    public string Status
    {
        get => _status;
        private set => SetField(ref _status, value);
    }

    public RelayCommand GoCommand { get; }
}
```

`RelayCommand` takes an optional `canExecute`; call `RaiseCanExecuteChanged()` when its inputs
change, or the button stays stale.

Everything bound must be touched on the UI thread. That is the single most common source of
random crashes in a plugin.

---

## 9. Lifetime

Activation builds your view. Deactivation:

1. cancels all your background work,
2. retracts all your notifications,
3. removes the tab,
4. disposes the view **and** its `DataContext` if either implements `IDisposable`.

So implement `IDisposable` on your view model and release what you hold: bitmaps, watchers,
processes:

```csharp
public void Dispose()
{
    _scanTask?.Dispose();
    _preview?.Dispose();
    _process?.Kill(entireProcessTree: true);
}
```

Anything you started that the shell does not know about is yours to stop. Background tasks
registered through `IMeowsBackgroundWork` are already handled.

---

## 10. How the shell finds you

One subfolder per plugin inside a `plugins` directory, resolved in this order:

1. `MEOWS_PLUGINS_DIR`, which is `;` separated and additive, see
   [Developing a plugin in its own repository](#developing-a-plugin-in-its-own-repository)
2. `plugins` next to the executable, the deployed layout
3. `plugins` in any ancestor directory, **provided it holds a subfolder containing a `.dll`**

That last condition exists because Windows path matching is case-insensitive: without it, an
ancestor *source* folder named `Plugins` wins. It is why the shell's own loader lives in
`Meows/PluginSystem/` and not `Meows/Plugins/`.

Inside your folder the shell prefers `<foldername>.dll` and otherwise scans every `.dll`.

Three folder names are the installer's and are never scanned: `<Name>.installing` while a zip
is being written out, `<Name>.update` for a newer version waiting beside one that is loaded, and
`<Name>.old` for the one it replaced or uninstalled, until that can be deleted. Before each scan
the shell swaps every `.update` in and clears every `.old` it can. The installer also leaves
`meows-install.json` in a folder it made, and that note is the only thing that tells an
installed plugin from one copied by hand: only a folder with it gets *Uninstall* on its card
and the daily look for a newer release.

### Assembly isolation

Each plugin loads in its own `AssemblyLoadContext`, so two plugins can depend on different
versions of the same library. Anything starting with `Meows.Plugins.Abstractions`, `Avalonia`,
`System.`, or `Microsoft.` is deliberately **shared** with the shell.

That sharing is not an optimisation. A plugin that loaded its own Avalonia would return a
`Control` that is a *different type* from the one the shell can host, and activation would fail
in a thoroughly confusing way. This is also why the csproj marks those references
`ExcludeAssets="runtime"`.

Private dependencies are fine. Ship them in your folder and the resolver finds them.

---

## 11. Checklist

- [ ] `Id` is stable and unique
- [ ] Added to the solution
- [ ] `OutputPath` differs between Debug and Release
- [ ] Avalonia and the contract are `ExcludeAssets="runtime"`
- [ ] View model implements `IDisposable` and releases everything
- [ ] Every `SetCondition` has a matching `ClearCondition`
- [ ] Background work honours `context.Token`
- [ ] UI state is only touched on the UI thread
- [ ] Settings use `?? new()` on load
- [ ] No secrets in settings
- [ ] Colours come from `{DynamicResource Meows...}`, not from hex
- [ ] Strings come from a catalogue, with `WithCulture` and `LogicalName` set on the resource
- [ ] A `LanguageWatch` is held and disposed, so an open tab follows a language change
- [ ] No translated text captured into a field at construction
- [ ] `IGlanceable`, if the tab has a header line worth repeating on Home
- [ ] `Dispose` can be called twice: the shell disposes the view and then its `DataContext`, and
      most views dispose their `DataContext` themselves
- [ ] Added to `ViewSmokeTests` in `Meows.Tests`, which builds every plugin's view headless in
      both themes and both languages and fails on anything Avalonia complains about

## 12. Debugging

`%APPDATA%\Meows\meows.log` is truncated per run and shows discovery:

```
[plugins] Scanning F:\...\Meows\plugins
[plugins] Found 'Purrge' (meows.purrge).
[plugins] Discovered 2 plugin(s).
```

Not appearing at all usually means the wrong `plugins` folder, or a build that never ran because
the project is not in the solution. "implements IMeowsPlugin but has no parameterless
constructor" means exactly that. Delete `%APPDATA%\Meows` for a clean reset, since nothing is written
inside the repository.
