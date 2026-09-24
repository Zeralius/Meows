# Nest

*What actually matters if the drive dies.*

Most of what is on this machine can be downloaded again. A little of it cannot: game saves,
Wonderdraft and Dungeondraft maps, project files, SSH keys, a bot's config, mod profiles. Nest
finds those, says how much there is in total (usually a surprisingly small number), and how long
since each was copied somewhere else. Every other plugin here answers what can go; this one
answers what must never go.

Plugin id `meows.nest`.

## Where it looks

A starting list of where saves and settings usually are: Saved Games, Documents\My Games,
AppData\LocalLow (where Unity games keep saves), Minecraft worlds, Wonderdraft, Dungeondraft,
Adobe projects, `.ssh`, and Steam's per-account `userdata`. Only the ones that exist on this
machine are shown, and each can be switched off. **The list is a starting point, never the
authority**: games move their saves and some keep them in the install folder, so any other folder
can be added, and the tab says plainly that the list is not complete.

Each place is measured without following links, so a junction inside a save folder never pulls
another drive into the count.

## Copies

Choose where copies go: another drive, a memory stick, a share. Each place gets a folder of its
own there (an added folder's name carries a short hash of its path, so two folders called Saves do
not share one). **Copy now** copies what is new or changed, each file written under a temporary
name, checked against what was read, and only then given its name and date. Nothing in the copy is
ever deleted, so a save deleted here by mistake is still there. A file a game has open and locked
is named, and the rest still go.

A file counts as copied when the copy has it with the same size and date. Each place says whether
it was never copied, is up to date and since when, or how many files have changed since the copy.
A copy folder inside one of the places is refused; one on the same drive as everything it copies
is allowed with a warning, because it still guards against a deleted file, just not against the
drive.

## Home

With no copy folder, the card says how much cannot be downloaded again and that it has never been
copied. With one, it turns red once changes have gone uncopied for a month. Each copy is a
*copied* line in the history, so a rule can follow it.

## With no window

`Meows.exe --do nest.copy` does Copy now from Task Scheduler: a memory stick left in overnight, or
a drive that is only plugged in on Sundays. It declines, with the reason, when no copy folder is
set or its drive is not there, and a file it could not copy comes up as a Windows notification.

## What it is not

Not a backup program: no schedule of its own, no versions, no restore. It is the short list of what
a backup has to include, and the plain question of whether it did.
