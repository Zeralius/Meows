# Chonk

Measures where the room on a drive went, biggest first, and clears out what you no longer want.

Plugin id `meows.chonk`. Windows only, because deletion goes through the shell's Recycle Bin.

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

## What that folder actually is

Finding a 40 GB folder called `Library`, `blob_storage` or `shader_cache` is only half an answer.
The other half is what put it there, whether anything is still using it, and what breaks if it
goes. Selecting anything works that out and says so in the right hand panel, along with the
reasons, so you can disagree with it.

| It says | Meaning |
|---|---|
| **Rebuildable** | Build output or a cache. Removing it costs time while something rebuilds it, and nothing else |
| **Application data** | A program's own settings and state. It will not usually break, but it will forget what was in here |
| **Game** | Installed through Steam. Its launcher has to remove it, not you |
| **Yours** | One of your own folders, or contents that read like documents and media |
| **Not sure** | Nothing could be established. Said plainly rather than guessed at |

The answer comes from evidence rather than a table of known folder names: what is inside it, which
application's folder it sits in, how recently anything wrote to it, and whether a running program
is holding a file open. A table would need updating forever and would still be wrong about
anything it had not heard of.

Steam is the exception, because there the disk is not the best witness. A game folder has a
manifest beside it holding the real name, the size and when it was last launched, so the panel can
say *"Call of Duty®, installed through Steam, last played 4 days ago"* rather than *"a large
folder of game data"*. That one is worth calling out because deleting a game folder by hand leaves
Steam still believing the game is installed, and the confirmation says so before you do it.

Anything held open by a running program, and anything belonging to a launcher, is marked as worth
a second look. Where nothing can be established it says so: a confident wrong answer here gets
something deleted.

## Removing things

**It asks first.** The confirmation says what is about to go, how big it is, and for a folder how
many files are inside it, because the name alone does not tell you a folder holds four thousand
things. Escape or Cancel backs out. Nothing is touched until you say yes.

Tick **Do not ask again** in that box, or untick **Ask before deleting** in the header, and it
stops asking. The two are the same setting, so turning it off in the box leaves the header
unticked and you can turn it back on there without going near a settings file. It is remembered.

**Everything goes to the Recycle Bin**, never `File.Delete`, and a folder goes whole with
everything inside it. This is the same code Purrge deletes through, which is why it lives in
`Meows.Disk/` rather than in either plugin: two copies of a destructive operation is how one of
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
