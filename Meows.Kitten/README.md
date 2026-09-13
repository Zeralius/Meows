# Kitten

Makes another one. A developer tool, not a plugin: it writes a new in-tree plugin from a name,
the way the fifteen before it were written by hand, and then proves the result builds.

```bash
dotnet run --project Meows.Kitten -- Whiskers --plain "Print queue" --icon 🧵 --category group.disk
```

## What it writes

`Meows.Plugins.Whiskers/` with the csproj (Debug output into `plugins/`, the string catalogues
embedded under the name the shell looks for, Avalonia and the contract kept out of the output),
`WhiskersPlugin.cs`, a view model with the header state, the error strip, the language watch, the
search hook and the dispose, a view with the header bar and an empty state, `Strings.en.json` and
`Strings.de.json`, a README in the shape of the others, and `Meows.Tests/WhiskersTests.cs` with
two tests that pass on day one.

## What it edits

Six places, which is every place a plugin has to be known, plus the two the house rules ask for:

| | |
|---|---|
| `Meows.sln` | through `dotnet sln add`, because a solution file has GUIDs nobody should type |
| `Meows.Tests/Meows.Tests.csproj` | a project reference, so the assembly lands beside the tests |
| `Meows.Tests/ShippedPlugins.cs` | one `typeof` line at the `// kitten:` marker; every plugin-wide test walks that list |
| `README.md` | a row in the table, above the four bot plugins so the sentence under the table stays true |
| `Meows/Meows.csproj` | the version, minor bumped, since a new plugin is a minor by the README's own rule |
| `CHANGELOG.md` | a stub under a heading for that version, when the checkout keeps one; it is gitignored, so a fresh clone has none and Kitten says nothing |

The release workflow needs nothing: it finds `Meows.Plugins.*` on disk, and the packaged app is
asked to load what was staged.

## Why it builds afterwards

A generator that falls behind the thing it generates is worse than none, because it produces
something that looks finished and is not. So Kitten builds the new project, builds the test
project, and runs the tests that know every plugin: the catalogue checks, the view smoke test in
both themes and both languages, the plain-name check, the search check, and the new plugin's own.
If the template has drifted from what the shell expects, that fails in the same terminal, before
anything is committed. `--no-verify` skips it; `--dry-run` only says what would happen.

It refuses a name that is not one PascalCase word, a folder that already exists, and a category
the Plugins tab does not know. It never deletes anything: if the build fails, the files stay for
fixing, and the four edits are small enough to undo by hand or with `git checkout`.

## Options

```
<Name>                 PascalCase, one word, the cat's name: Whiskers, Larder, Prowl.
--plain <text>         What it is in plain words, for the paw switch. Default: the name.
--plain-de <text>      The same in German. Default: the English one.
--description <text>   One sentence for the card. Default: a placeholder.
--description-de <text>
--icon <emoji>         Default: 🐾
--category <key>       group.everyday (default), group.disk or group.bot.
--no-verify            Write and edit but do not build or test.
--dry-run              Say what would be written and edited, and stop.
```

## What it is not

Not the `dotnet new` template in `template/`. That one is for a plugin written outside this
repository against the NuGet package, and drops into a `plugins` folder. Kitten is for a plugin
that lives here, ships in the release, and has to be on every list the tests keep.
