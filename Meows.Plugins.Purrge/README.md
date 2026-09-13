# Purrge

Finds files with identical content anywhere on the machine, groups them, and clears out the
copies you do not want. And, since it already knows how to tell two files apart cheaply, checks
that a backup copy is really complete and identical.

Two modes on one tab, **Duplicates** and **Compare**, switched at the top. They share the folder
tree and the staged content check and nothing else.

Plugin id `meows.purrge`. Windows only, because deletion goes through the shell's Recycle Bin.

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

## Compare: is the copy really a copy

Pick the folder the copy was made from as the **source**, the folder it lives in as the **copy**,
and press Compare. Everything in the source is looked for in the copy by its relative path, and
each thing that is not right is one row, worst first:

| | Meaning |
|---|---|
| **Missing** | In the source, not in the copy |
| **Stale** | In both, different, and the copy is older. The copy fell behind |
| **Different** | In both, different, and the copy is *not* older. Something changed on the copy side, which is the one to look at hardest |
| **Extra** | In the copy, not in the source. Not wrong, reported last, and does not count against the copy |
| **Unreadable** | One side could not be opened, so nothing can be said |

The check is the duplicate scan's staged one turned to face two roots. Sizes are compared first,
and a pair whose sizes differ is settled without either file being opened. Then the first 64 KB,
then the whole file, only for pairs that still agree.

**Trust size and date** skips the read for pairs whose size and modified time both match, which
is what most backup tools assume and is fast. Left off, both files are read, which is the only
way to catch a copy that went bad without anything touching its date. Either way the result says
how many files were actually read in full, so a quick run cannot be mistaken for a thorough one.
Modified times are allowed to differ by two seconds, because FAT rounds to that and some copies
land a tick apart.

A folder cannot be compared against one inside it; the copy would be compared against itself and
always pass.

**It only reads.** There is no copy button, no delete, no touching of dates, and there will not be.
The moment it fixed what it found it would be a backup tool with all of a backup tool's failure
modes, and the honest scope that makes it worth having would be gone. The two buttons under the
preview open Explorer on the source and on the copy; fix things with the tool that made the copy.

## Not in this version

**Hardlinks and junctions.** Several paths can point at one file. Deleting one is harmless
because the data survives through the others, but it frees nothing, so those sets are busywork.

**Perceptual matching.** Only exact content matches are found today. A re-encode, a resize or a
re-save at different quality is a different file and will not be grouped.

## Settings

`%APPDATA%\Meows\plugins\meows.purrge\settings.json` holds the last scan root, the size floor, the
skip-system-folders flag, the age basis, which mode the tab was in, the last source and copy, and
whether size and date are trusted.
