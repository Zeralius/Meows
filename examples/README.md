# Example plugins

Five small plugins, each built around one part of the contract, sitting between the template's
hello-world and the plugins that ship. Every one compiles in CI and has its view built headless
in both themes and both languages by the test suite, so what is here is what works against the
contract this repository ships.

| Example | What it shows |
|---|---|
| **[Meow](Meow/README.md)** | The least a plugin can be: one class, one control, nothing optional |
| **[Yowl](Yowl/README.md)** | A schedule, a condition with two buttons and one key, settings, a warning in the log, a line on Home |
| **[Nudge](Nudge/README.md)** | A handoff out with a reply, handoffs in through `IHandoffTarget`, a folder picked through the host |
| **[Yarn](Yarn/README.md)** | The store as a journal, Ctrl+K while on and while off, *Put back* from the History tab |
| **[Sniff](Sniff/README.md)** | Background work with progress and Cancel, the token honoured, the save dialog |

## Running them

They are in the solution. A Debug build of the solution drops each one into `plugins/` beside
the real plugins, so `dotnet run --project Meows/Meows.csproj` shows all five on the Plugins tab
under an **Examples** heading, switched off like everything else. They are not part of the
release: the release workflow stages `Meows.Plugins.*` and nothing under `examples/`.

## Starting from one

Copy the folder out of the repository, and in the `.csproj` replace

```xml
<ProjectReference Include="..\..\Meows.Plugins.Abstractions\Meows.Plugins.Abstractions.csproj"
                  Private="false"
                  ExcludeAssets="runtime" />
```

with

```xml
<PackageReference Include="Meows.Plugins.Abstractions" Version="1.5.0" ExcludeAssets="runtime" PrivateAssets="all" />
```

Then rename, change the `Id`, and go. The rest of the file is what the template writes.
[PLUGIN-GUIDE.md](../PLUGIN-GUIDE.md) is the whole contract; each example's README says which
section it belongs to.
