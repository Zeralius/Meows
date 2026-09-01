# Litter

Sorts out the downloads folder: what arrived today, what has been rotting for months, and what
never finished downloading at all.

Plugin id `mews.litter`. Windows only, because deletion goes through the shell's Recycle Bin.

## The two columns

| | |
|---|---|
| **Left** | Buckets to filter by, each saying how many items and how much room. Ages first, then kinds |
| **Right** | What matches, biggest first, with age and size. Ctrl and shift pick several |

Defaults to `%USERPROFILE%\Downloads`, which is where this is nearly always pointed. **Folder...**
changes it and it is remembered.

## What it shows

Only the top level. A folder counts as **one row carrying everything inside it**, because an
extracted archive of a thousand files is one thing to decide about, and its size is what removing
it would actually free.

Buckets only appear when they have something in them, so an empty category never takes up space.
Ages are Today, This week, This month and Older. Kinds come from the extension: installers,
archives, images, video, audio, documents, and everything else.

**Unfinished** is its own kind, for `.crdownload`, `.part`, `.partial` and `.download`. Those are
always junk, and separating them means the one category you can clear without thinking is one
click away.

Sorted biggest first, because in a downloads folder the question is always what is costing the
most rather than what is newest.

## What it does not do

**It does not decide for you.** It names what a thing is and how old it is, and stops there. A
tool that guesses which downloads you are finished with will guess confidently and be wrong, and
the cost of being wrong here is a file you wanted.

## Removing things

Same safeguard as Chonk. It asks first, saying how many items, how much room, and how many of
them are folders that go whole with everything inside. Escape or Cancel backs out. **Do not ask
again** in the box, or **Ask before deleting** in the header, turns it off, and they are one
setting so it can be turned back on without touching a settings file.

Everything goes to the Recycle Bin through the same `Mews.Disk` code Purrge and Chonk delete
through, so it can all be brought back.
