# Scoop

*Goes through what was thrown out.*

Every drive's Recycle Bin in one list: what is in it, how big each thing was, where it came
from, and how long it has been sitting there. Biggest first, because the question the tab exists
to answer is where the room went.

Every other plugin here ends its rows at the bin. Chonk, Litter, Molt, Mouser, Purrge and Kibble
all delete through `Meows.Disk.RecycleBin`, which is the only deletion path in the codebase, and
none of them can see what happened to any of it afterwards. The walker makes it worse on purpose:
`WalkRules` skips `$Recycle.Bin` on every scan, so Chonk measures a drive while stepping over the
one folder holding everything already decided against. On the machine this was written on that
was 72 GB.

## What it shows

Left, one line per drive: how much its bin is holding and how many things are in it. Right, the
things themselves, with the folder each came out of, its size, and when it went. The header adds
it all up, and the same line goes on the Home card, so "there is 40 GB lying about in bins" is
answered without opening the tab at all.

A drive whose bin will not open is still listed, with the reason. *Could not be read* and *is
empty* are different answers and only one of them is good news.

## The two verbs

**Put it back** moves one thing to where the bin says it came from, making the folder again if
that has gone. It refuses when something is standing at the original path now, and says so rather
than overwriting: the file that is there is newer than the one in the bin, by definition.

This deliberately does not use the shell's own undo. That restores the last operation, which is
not the same as restoring a row somebody picked out of six months of deletions, and it would
quietly do something else whenever the two disagreed.

**Empty** takes one drive's bin, through the shell so its own bookkeeping stays right, and asks
once first: the first press changes the button's words, the second does it. It is per drive and
there is no *empty all*. This is the only thing in Meows that cannot be undone, which is worth
saying out loud in a tab whose every neighbour ends at a bin that can.

## How it reads

Each drive keeps its bin at `<drive>\$Recycle.Bin\<SID>`, one folder per account, and inside it
every deleted thing is two entries sharing a name: `$I` holds the size, the time and the original
path, and `$R` is the thing itself. Only the current account's folder is read; the others belong
to other people and reading them needs rights this application has no business asking for.

The `$I` format is the one part that can be subtly wrong rather than obviously broken. A misread
path length gives a plausible-looking path, and a restore would then put the file somewhere
nobody asked for, so it is parsed from bytes in a function of its own and pinned by tests:
both header versions, a path with an umlaut in it, a truncated file, a length that lies, and a
timestamp that is not a timestamp. An entry that cannot be read costs its own row and nothing
else.

The reading lives in [`Meows.Disk/RecycleBinContents.cs`](../Meows.Disk/RecycleBinContents.cs),
beside the deletion path it is the other half of.
