# Purrge

Finds files with identical content anywhere on the machine, groups them, and clears out the
copies you do not want.

Plugin id `mews.purrge`. Windows only, because deletion goes through the shell's Recycle Bin.

## The three columns

| | |
|---|---|
| **Left** | Folder tree. Pick the root to scan. Children load on expand, so opening a drive never enumerates the whole thing |
| **Middle** | Duplicate sets, ordered by how much space each would free. Every file shows its thumbnail, folder, and *both* timestamps |
| **Right** | Preview of the selected file, then the actions |

Under the preview: **Keep oldest**, **Keep newest**, **Delete selected file**, and **Show in
Explorer**.

## How the scan works

Reading every file would make a whole-drive scan unusable, so each stage only touches what
survived the last one.

1. **Group by exact size.** A size held by only one file cannot contain a duplicate, so that
   file is never opened. This eliminates almost everything.
2. **Hash the first 64 KB** of what is left. That separates same-size files cheaply.
3. **Hash in full**, but only for what still collides.

Files under 4 KB are skipped, as are `Windows`, `Program Files`, `ProgramData`, recycle bins,
`node_modules`, `.git`, `bin` and `obj`. Those hold duplicates that are supposed to exist.

Directory reparse points are skipped so a junction pointing at a parent cannot send the walk
round in circles. An unreadable file or folder is passed over instead of aborting the scan.

## Deleting

**Everything goes to the Recycle Bin**, never `File.Delete`. This tool removes files in bulk on
the strength of an automated judgement, so every removal has to stay recoverable. Deletion is a
`SHFileOperation` with `FOF_ALLOWUNDO`, and the result is checked against the filesystem rather
than trusted from the return code. A partial failure stays visible instead of quietly dropping
rows from the list.

**A set always keeps a survivor.** Keep oldest and keep newest leave one by construction, and
*Delete selected file* switches off once a set is down to its last copy. There is no path
through the UI that removes every copy of something.

## Oldest and newest

Copying a file does different things to its two timestamps, so the buttons say which one they
are using and every row shows both.

| Basis | What it means |
|---|---|
| **Modified** (default) | Usually survives a copy, so the original and its copies often agree |
| **Created** | Set fresh when the copy is made, so the copy looks newer than the original |

Neither is right in every case. That is exactly why it is a visible choice rather than a hidden
assumption. The setting persists.

## What the preview is for

For byte-identical matches the visual check is reassurance, not verification. Files with
matching hashes *are* the same file and no amount of looking changes that.

The pane starts earning its keep the moment perceptual hashing is added, to catch re-saves at a
different quality or size. There the files genuinely differ and your eye is the only thing that
can decide.

## Not in this version

**Hardlinks and junctions.** Several paths can point at one file. Deleting one is harmless
because the data survives through the others, but it frees nothing, so those sets are busywork.

**Perceptual matching.** Only exact content matches are found today. A re-encode, a resize or a
re-save at different quality is a different file and will not be grouped.

## Settings

`%APPDATA%\Mews\plugins\mews.purrge\settings.json` holds the last scan root, the size floor, the
skip-system-folders flag, and the age basis.
