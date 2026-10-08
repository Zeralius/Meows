# Screenshot

Sorts game screenshots by game: duplicates out, best-of picks to Scruff.

Plugin id `meows.screenshot`.

## What it does

Watches the folders screenshots land in: Steam's per-game folders under every library's
`userdata`, the Game Bar's Captures, Windows' own Screenshots, the Videos folder ShadowPlay
defaults to, ShareX (its default folder and wherever its config points), Minecraft, and any
folder added by hand. New sources arrive switched on; a drive that is unplugged today gives
nothing rather than an error, and nothing kept is ever dropped because of it.

Each scan groups by game, newest first, names identical bytes (all but the newest of each
set) and marks bursts taken seconds apart. Tick a star to keep a best-of pick — keeps survive
rescans until their file is gone — and send the keeps to Scruff, which is what a posting
queue wants. Copies go through the Recycle Bin, one by one or all at once, said out loud
first. Every scan, keep, send and recycle is a line in the History tab.

A rule can ask for the copies (`screenshot.recycle-duplicates`), and
`Meows.exe --do screenshot.scan` reads the folders with no window.

## What it refuses to do

No recordings (they live beside the shots and are left alone), no image grading beyond
"largest of the burst", no editing, and nothing is ever deleted outright: the Bin or nothing.
