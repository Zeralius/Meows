# Kit

Turns a folder of maps, tokens and handouts into a one-shot the table can use: named, gridded or
gridless, fitted, framed, and handed to Foundry or Roll20 the way each wants it.

Plugin id `meows.kit`. Plain name **One-shot kits**.

## A kit is a folder

`Oneshots\<name>\` with `maps`, `tokens`, `handouts` and `notes`, and a `kit.json` for what a
file name cannot hold: the grid, the order the evening reaches things, captions. Delete the
manifest and it is still a folder of pictures. Every change on the tab is a change to a file;
the manifest only remembers. Anything Kit alters keeps the picture it started from under
`originals`, once, so the first original is always the one you can go back to.

*New kit* makes the folder. *Add pictures…* copies pictures in, as maps, tokens or handouts
according to the dropdown beside it; a folder dropped straight into `maps` and *Refresh* does the
same. Files handed over from another tab (Kibble's right-click, Birdwatch's saves) land the same
way.

## Maps

**Rename in place.** The name on the card is the name the VTT shows, and *Rename* (or Enter)
renames the file to match. A clash with a file already there is refused, not overwritten.

**Grid, or none.** The one number that matters for a battle map is pixels per square, and nobody
knows it. The preview draws blue lines over the map at the size and offset on the card; *Guess*
starts from 70, 100 or 140, whichever divides both sides most nearly into whole squares, and the
eye confirms. The size in squares falls out of that. **Untick *This map has a grid* for a map
played without one**: the VTT gets a gridless scene measured in pixels, which is a choice and is
exported as one, not as a missing number.

**Fit.** Down to a chosen longest side, as WebP, or PNG when there is transparency to keep. The
grid number and the offset scale with the picture, so the squares stay squares. A 40 MB PNG is
still the commonest reason a player's browser crawls.

**Frame.** A border *outside* the picture: the canvas grows by the frame's thickness on every
side and the map itself is untouched, so the grid does not move; the padding is recorded and the
exports offset the grid by it. Three borders ship, parchment, stone and torn paper, drawn as
9-slice tiles; nicer ones are a matter of better PNGs under `Frames` with the same names.

## Tokens

The way the token makers on the web do it: the picture is cut to the circle inside the ring, the
ring is drawn on top, and **everything outside the ring is transparent**. Zoom and the two
sliders move the picture inside the circle, with the preview showing exactly what will be
written. *Make the token* writes a 512 × 512 PNG over the file. Four rings ship: gold, silver
double, crimson, studded iron; *no frame* cuts the circle and nothing else. *Side* is a word the
Foundry module turns into the token's disposition.

## Handouts

Pictures the players get to see, framed anywhere they like, with a caption the VTT shows.
Markdown files under `notes` travel as journal entries.

## Where it goes: Foundry

*Write for Foundry* writes a folder named after the kit under the exports folder: every picture,
and a `kit.json` with the numbers worked out, one scene per map with its size in pixels and in
squares, its grid or the fact that it has none, its offset, the order. The **meows-kit** module
in [Zera's VTT Forge Tools](https://github.com/Zeralius/Zeras-vtt-forge-tools) reads that folder
from inside Foundry: upload it through Foundry's file picker, press *Import a kit*, and a Folder
named after the kit appears holding one Scene per map, gridded or gridless as the card said, one
JournalEntry per handout and per note, and one Actor per token with its picture and disposition.
Five maps in the kit, five scenes in a folder, in order.

There is no way to write into a running Foundry from outside, and writing its database while it
runs corrupts the world, which is why the module exists. Getting the folder to the server is the
one manual step until *Reach past this one machine* in IDEAS.md lands.

## Where it goes: Roll20

Roll20 has no way in: no API creates a page or uploads a picture, and the Pro-only Mod API runs
inside Roll20 and can do neither. So *Write for Roll20* does what Scruff does for the sites it
cannot post to: hands the files over spelled Roll20's way. Roll20 measures pages in 70-pixel
units, so each gridded map is resampled to exactly `columns × 70` by `rows × 70` and snaps to the
page grid on upload; a gridless map keeps its pixels and the sheet gives the page size in units.
Maps are JPEG under the per-file limit on the card (it moves with your plan, so it is a setting),
numbered in the evening's order; tokens are PNG; handouts JPEG. `ROLL20.md` beside them says
which page gets which size and which file, and that the library folder is made by hand.

## What it is not

A table. Running the session is the VTT's job; Kit stops at its door. And nothing here draws on
a map: a frame goes outside the playable area, always, or the artist's grid would be lost.
