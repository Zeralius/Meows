# Chonk

Measures where the room on a drive went, biggest first, and clears out what you no longer want.

Plugin id `mews.chonk`. Windows only, because deletion goes through the shell's Recycle Bin.

## The three columns

| | |
|---|---|
| **Left** | Every ready drive, with how much of it is used |
| **Middle** | What is inside the folder you are looking at, biggest first, with a breadcrumb above it |
| **Right** | The selected item, and what you can do with it |

Double click a folder to go into it. **Up** and the breadcrumb come back out.

## What it measures

Sizes only. **No file is ever opened**, which is what makes this far cheaper than a Purrge scan
over the same tree: Purrge reads contents because identical bytes are its whole point, and Chonk
only ever needs what the directory walk already returns.

A folder's size is everything underneath it, so the number next to a folder is what you would
actually get back by removing it. The percentage is its share of the folder it sits in, which is
why it changes as you drill down.

**Files under 1 MB are counted but not listed one by one.** They collapse into a single row
saying how many there are and what they add up to. A folder of ten thousand thumbnails is worth
one line, not ten thousand, and every byte is still in the total. That row cannot be deleted or
opened, because it stands for many files rather than one thing.

The same folders Purrge skips are skipped here: `Windows`, `Program Files`, `ProgramData`,
recycle bins, `node_modules`, `.git`, `bin` and `obj`. Untick **Skip system folders** to count
them anyway. Directory reparse points are never followed, so a junction pointing at one of its
own parents cannot send the scan round in circles, and an unreadable folder is passed over rather
than aborting the whole thing.

The scan runs as background work, so switching tabs does not abandon it and the **Tasks** panel
says how far it has got.

## Removing things

**Everything goes to the Recycle Bin**, never `File.Delete`, and a folder goes whole with
everything inside it. This is the same code Purrge deletes through, which is why it lives in
`Mews.Disk/` rather than in either plugin: two copies of a destructive operation is how one of
them gets a fix and the other does not.

After something is removed, its size is taken off every folder above it and the list is redrawn
from what is left. Nothing is rescanned, because the answer is already known.

## What it is not

WizTree and WinDirStat exist and are good. WizTree in particular reads the NTFS master file table
directly rather than walking folders, and **Chonk does not try to beat it on speed**.

The argument for this one is that it is already open in an app you already have, and that what it
finds is next to the tools that deal with it: a fat folder full of near-duplicates is Purrge's
problem, a fat folder of unsorted material is Kibble's. If that stops being a good enough reason,
this is a plugin worth not keeping.
