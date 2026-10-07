# Contributing to Meows

One issue, one branch, one review. File the issue first (the templates ask for
everything the work needs), then work on `feature/<n>-<slug>` branched from
`main`, then open a pull request back to `main`. Nothing lands on `main`
directly.

## Scope discipline

The issue's non-goals are binding. If the work uncovers something adjacent that
wants doing, file it as its own issue rather than growing the branch.

## House patterns

- One plugin is one tab. Settings are plain JSON through the host.
- History lines read in the plugin's own words.
- Rules actions and recorded kinds wherever something can be asked for or
  waited on; a watch, schedule, or `--do` job wherever one makes sense.
- One string catalogue per language. German says everything English says,
  with the same placeholders.
- Views hold together in both themes and both languages.

## Verification

Build, then run the new tests plus every affected suite:

```bash
dotnet build
dotnet test Meows.Tests/...
```

A red suite is either fixed or proven pre-existing: stash the work, rerun the
suite on the clean tree, restore, and say which it was in the pull request.

## Versions and docs

Do not bump versions or write changelogs unless asked. Plugin work adds its
README and its row in the root README table; contract work updates
PLUGIN-GUIDE.md and the versioning notes in README.md.
