# Writing a Meows plugin

Everything useful in Meows lives in a plugin. The shell finds them, lets you activate them, and
gives each one a tab. That is nearly all it does.

This guide covers the whole contract. For the shape of a finished plugin, read
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
Meows: the **contract**, and a way for the shell to **find** it. Both are available from outside.

**1. Get the contract as a package.** From the Meows repo, once per contract version:

```bash
dotnet pack Meows.Plugins.Abstractions/Meows.Plugins.Abstractions.csproj -c Release -o artifacts/nuget
```

**2. Point your own repo at that folder** with a `nuget.config` beside your `.csproj`:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="meows-local" value="F:\path\to\Meows\artifacts\nuget" />
  </packageSources>
</configuration>
```

**3. Your `.csproj` references packages only.** No `ProjectReference`, no Meows sources:

```xml
<ItemGroup>
    <PackageReference Include="Avalonia" Version="12.1.1" ExcludeAssets="runtime" />
    <PackageReference Include="Meows.Plugins.Abstractions" Version="0.2.1" ExcludeAssets="runtime" />
</ItemGroup>
```

`ExcludeAssets="runtime"` still matters, for the same reason as in-tree: the shell supplies both
at runtime, and a second copy of Avalonia in your output would make your `Control` a different
type from the one the shell can host.

**4. Tell the shell where your build lands.** `MEOWS_PLUGINS_DIR` takes a `;`-separated list and
is **additive**, so your plugin loads *alongside* the built-in ones rather than instead of them:

```bash
MEOWS_PLUGINS_DIR="C:\dev\HelloMeows\meows-plugins"
```

It still needs one subfolder per plugin, so copy your DLL to
`...\meows-plugins\HelloMeows\HelloMeows.dll`. The log then shows both roots being scanned:

```
[plugins] Scanning C:\dev\HelloMeows\meows-plugins
[plugins] Found 'Hello' (external.hello).
[plugins] Scanning F:\...\Meows\plugins
[plugins] Found 'Purrge' (meows.purrge).
```

Everything else behaves identically: your own `%APPDATA%\Meows\plugins\<your-id>\` data
directory, notifications, background work.

#### Version compatibility

`Meows.Plugins.Abstractions` is deliberately **shared** with the shell rather than loaded from
your folder, and that is what makes type identity work across the load context boundary. The
consequence: at runtime your plugin uses the shell's copy, not the one you compiled against.

The shell checks this for you. At discovery it reads the contract version your assembly was
compiled against and refuses anything it cannot honour, **before constructing your plugin**, so
none of your code runs. The reason appears on your plugin's card in place of its toggle:

> Built for Meows contract 0.3.0, which is newer than this shell's 0.2.1. Update Meows, or rebuild
> the plugin against 0.2.1.

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
fine. Telegram Poster and Kibble need `Meows.Bot.Core.dll` this way, and Purrge and Chonk
need `Meows.Disk.dll`:

```bash
for p in Meows.Plugins.TelegramPoster Meows.Plugins.Purrge Meows.Plugins.Kibble Meows.Plugins.Chonk; do mkdir -p "artifacts/Meows-win-x64/plugins/$p" && cp "$p"/bin/Release/*.dll "$p"/bin/Release/*.deps.json "artifacts/Meows-win-x64/plugins/$p/"; done
```

Avalonia and `Meows.Plugins.Abstractions` are not in that output to be copied, because the csproj
files keep them out. That is on purpose: the shell has to be the only source of both.

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

## 3. The entry point

```csharp
public sealed class MyPlugin : IMeowsPlugin
{
    public string Id => "meows.my-plugin";   // stable forever: it is the settings key
    public string DisplayName => "My Plugin";
    public string Description => "One sentence, shown on the Plugins tab.";
    public string? Icon => "🎲";            // shown on the tab header
    public string? Category => "Everyday";  // heading on the Plugins tab, optional

    public Control CreateView(IMeowsHost host) =>
        new MyView { DataContext = new MyViewModel(host) };
}
```

Public, with a parameterless constructor, since the shell instantiates it by reflection. `Id` is
the identity for stored settings and activation state, so changing it later orphans both.

`Category` is optional and has a default, so leaving it out compiles and loads exactly as before;
the plugin simply appears under **Everything else**. The shell does not interpret the text and
holds no list of valid groups: two plugins share a heading when they spell it the same way,
ignoring case. Pick an existing one to join it, or invent your own.

`CreateView` runs once per activation. If it throws, the shell catches it, marks the plugin
*Failed* on its card, and logs the exception; a broken plugin cannot take the window down.

---

## 4. `IMeowsHost`

Your whole view of the shell. One instance per plugin, scoped to you.

```csharp
public interface IMeowsHost
{
    string PluginId { get; }
    string DataDirectory { get; }
    void Log(string message);
    T? LoadSettings<T>() where T : class;
    void SaveSettings<T>(T settings) where T : class;
    IMeowsNotifications Notifications { get; }
    IMeowsBackgroundWork Background { get; }
}
```

### `DataDirectory`

A writable folder at `%APPDATA%\Meows\plugins\<your-id>\`, created before you see it. Put caches
and databases here. Never write inside the repository or next to the executable.

### `Log`

Goes to the shared log pane and `%APPDATA%\Meows\meows.log`. Safe from any thread. Use it for a
trail you would want when something misbehaves, not for anything the user must act on. That is
what notifications are for.

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
*whether* one exists.

---

## 5. Notifications

Use these when the user needs to know something. The point is that the shell owns one surface,
so a problem raised by a tab in the background is still seen.

```csharp
public interface IMeowsNotifications
{
    void Post(NotificationSeverity severity, string title, string message = "",
        NotificationAction? action = null);

    void SetCondition(string key, NotificationSeverity severity, string title,
        string message = "", NotificationAction? action = null);

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

### Notification or in-tab banner?

| | |
|---|---|
| **Notification** | Environmental or time-shifted: a missing tool, a crashed process, a finished import. True whether or not your tab is open |
| **In-tab** | Contextual to what is on screen: which group is misconfigured, which file is selected |

Purrge posts a notification when a scan finishes, because scans are long and you will be
elsewhere. It draws duplicate sets in the tab, because they only mean anything there.

---

## 6. Background work

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

## 7. MVVM and threading

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

## 8. Lifetime

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

## 9. How the shell finds you

One subfolder per plugin inside a `plugins` directory, resolved in this order:

1. `MEOWS_PLUGINS_DIR`, which is `;` separated and additive, see
   [Developing a plugin in its own repository](#developing-a-plugin-in-its-own-repository)
2. `plugins` next to the executable, the deployed layout
3. `plugins` in any ancestor directory, **provided it holds a subfolder containing a `.dll`**

That last condition exists because Windows path matching is case-insensitive: without it, an
ancestor *source* folder named `Plugins` wins. It is why the shell's own loader lives in
`Meows/PluginSystem/` and not `Meows/Plugins/`.

Inside your folder the shell prefers `<foldername>.dll` and otherwise scans every `.dll`.

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

## 10. Checklist

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

## 11. Debugging

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
