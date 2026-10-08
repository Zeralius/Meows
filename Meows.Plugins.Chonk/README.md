# Chonk

Measures where the room on a drive went, biggest first, and clears out what you no longer want.

Plugin id `meows.chonk`. Windows only, because deletion goes through the shell's Recycle Bin.

## The three columns

| | |
|---|---|
| **Left** | Every ready drive, with how much of it is used, opening into the folders inside it |
| **Middle** | What is inside the folder you are looking at, biggest first, with a breadcrumb above it |
| **Right** | The selected item, and what you can do with it |

Double click a folder to go into it. **Up** and the breadcrumb come back out.

## Choosing what to measure

Open a drive on the left and the folders inside it appear under it, and so on down. Pick any of
them, or use **Folder…** for somewhere the tree does not reach, and it is shown at the bottom of
the panel as what **Scan** will measure. **Nothing is measured until Scan is pressed.** Clicking a
drive used to scan the whole thing on the spot, which is a long wait for a mis-click and a poor
way to measure one folder on it.

The tree lists a folder the first time it is opened and not before, so opening a drive costs a
directory listing rather than a scan. The recycle bin and other system folders are left out of
it, since they are not places anyone chooses to measure; hidden folders stay, because `AppData` is
hidden and is exactly the sort of place worth measuring. Where you last scanned is chosen again
when the tab opens, with the tree opened down to it, and still not measured.

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

## An archive beside what it unpacks to

`photos.zip` sitting next to `photos\` is one of the ways a drive quietly fills up: the same
material, paid for twice, and nothing on the drive says so. An archive in the list shows as one,
and when a folder of the same name is beside it, or an archive of the same name is beside a folder,
the row says that too. That much is read off the scan and costs nothing.

**Whether the two really hold the same thing is a content check, never a name match.** Select
either half and the archive is opened and held up against the folder: every entry has to be there,
the same size, the same first 64 KB, and then the same all the way through for whatever still
agrees. Only when all of that holds does the panel say the pair is paid for twice, with what the
archive costs on disk and what the folder holds of the same. An archive extracted three months ago
and edited since is not a twin, and the panel says what does not match instead.

When the folder holds more than the archive, the folder is still what the archive unpacks to, but
the confirmation says how many files would be lost with it and suggests removing the archive
instead. When the pair is exact, the confirmation says so, because that is the one time removing
a folder loses nothing.

Only zip archives, `.cbz` included, are opened. A `.rar` or `.7z` is still shown as an archive and
still pairs by name, but the panel says plainly that it cannot be compared here; 7-Zip can. There
is no extract and no repack: 7-Zip does both, and a Chonk that produces files is a different tool.

## Extract and check

A zip or cbz with nothing of its name beside it carries **Extract and check**. It asks first,
with the cost: how many files, how big unpacked, the new folder's name and the room left on the
drive. It refuses without writing anything when a folder of that name is already there, when the
archive wants a password, when there is nothing in it, or when the drive has no room for it.

The files go into a folder with a temporary name, which only gets the real one once every entry
is out, so a cancel or a failure halfway leaves the archive and nothing else. An entry whose name
would land outside the folder stops the whole thing. Afterwards every listed file is held up
against the folder with the same comparison that finds twins. When all of them are there byte
for byte, the archive says it is a twin, and the Recycle Bin button beside it, with its warning,
is how it goes. The history gets an *extracted* line either way, so a rule can follow it. Other
formats stay with 7-Zip.

## Wound up inside

The same table of contents answers two smaller questions, and the archive's panel says so when
either comes up. **An archive inside the archive**: a download of a download is packed twice, and
the line names it, or counts them, with how much of the archive they are. **One file and nothing
else**: the zip may be only a wrapper an upload tool put round it, and the file on its own would
do. A cbz of a single page is said more softly, because a comic of one page is sometimes meant.
Nothing is unpacked or changed; the finding is the point, and Extract and check is there when the
answer is to unwrap it.

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
