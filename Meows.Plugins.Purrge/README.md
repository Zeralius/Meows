# Purrge

Finds files with identical content anywhere on the machine, groups them, and clears out the
copies you do not want. And, since it already knows how to tell two files apart cheaply, checks
that a backup copy is really complete and identical.

Two modes on one tab, **Duplicates** and **Compare**, switched at the top. They share the folder
tree and the staged content check and nothing else.

Plugin id `meows.purrge`.

## Asked about particular files

A tab that is about to bin something can hand Purrge the files (a *files* handoff, Catnip does
it) and ask whether there is another copy. Purrge walks the drives those files sit on but opens
only files of exactly their sizes, so a drive with a million files costs a listing and a handful
of hashes, and answers in a sentence: *1 of 3 have another copy; the sets are listed*. Only sets
holding one of the asked files are shown. Windows only, because deletion goes through the shell's Recycle Bin.

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

## Look-alikes: the same picture, saved differently

Exact duplicates are identical bytes. Most duplication in a collection of downloaded pictures is
not: the same image saved twice at different sizes, re-encoded by a site, watermarked, converted
from PNG to JPEG. **Look-alikes** is a third mode for those. Every picture under the folder is
decoded small, once, and given a perceptual hash, and pictures within the chosen closeness of
each other form a group. Hashing, like everything else here, puts the access time back.

It is kept apart from the other two on purpose. An exact match is a fact; a look-alike is an
opinion, and two pages of one comic can look as close as two copies of one page. So:

- **The results are never mixed.** Byte-for-byte copies are left to Duplicates, and a group here
  never holds two identical files.
- **Closeness defaults to boring.** *Very close*, four bits in sixty-four, with *nearly identical*
  and *close* either side of it.
- **Groups do not chain.** Each group forms around one picture and holds what is close to that
  picture, so A like B and B like C does not make A like C.
- **Every file carries its own size and pixels**, since near copies differ by definition. The one
  suggested to keep has the most pixels, then the most bytes. **Keep this one instead** lets the
  better copy win.
- **Nothing is binned without looking.** There is no keep-one-bin-the-rest. A picture goes to
  the Recycle Bin one at a time, and only once it and the one being kept are both on screen at
  full size, side by side.

## Rename: tidy the names

The duplicates show how they got there: a browser's `(1)`, a `- Copy`, a transposed digit, a
hash-named re-save. **Rename** works on the files directly in the folder picked in the tree, in
this order: only the files matching the filter, the `(1)` and `- Copy` taken off, find and
replace (plain, or a regular expression), the case changed, and then the new name put together
from a template where `{name}` is what the steps left, `{n}` a number counted in name order and
`{date}` the day the file last changed. The extension is never touched; a name that lies about
its kind is Portion's to fix.

Every file is in the preview with its name now and after, before anything moves. A new name that
another file already has, two files heading for the same name, a name Windows would refuse, or a
regular expression that does not parse is marked on its row and sorted to the top, and **nothing
moves while any row clashes**. A file that is itself being renamed is no obstacle, so names can
swap or a sequence can shift: every file first steps aside under a temporary name and then takes
its new one. Dates stay as they were.

The last run is kept, across a restart too, and *Put the old names back* undoes it as far as the
files let it: a file that has gone, or whose old name something else now has, keeps its new one
and is named in the result. Each run and each undo is a *renamed* line in the history.

## Not in this version

**Hardlinks and junctions.** Several paths can point at one file. Deleting one is harmless
because the data survives through the others, but it frees nothing, so those sets are busywork.

**Perceptual matching.** Only exact content matches are found today. A re-encode, a resize or a
re-save at different quality is a different file and will not be grouped.

## When a rule asks

On the Rules tab Purrge offers **Look for duplicates in its folder**: the folder the other
plugin's event was about, or the folder of the file it was about, walked the way the Scan button
walks it, with the summary as the answer. A scan already running is left to finish. Each copy
Purrge sends to the Recycle Bin is an event a rule can wait for.

## Settings

`%APPDATA%\Meows\plugins\meows.purrge\settings.json` holds the last scan root, the size floor, the
skip-system-folders flag, the age basis, which mode the tab was in, the last source and copy, and
whether size and date are trusted.
