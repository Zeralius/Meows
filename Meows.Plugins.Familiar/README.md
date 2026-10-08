# Familiar

The wizard's cat, fetching things for the table. Maps gridded or gridless, tokens cut round with
a ring, handouts framed: on their own, on the bench, or as a one-shot kit with its run sheet,
handed to Foundry or Roll20 the way each wants it.

Plugin id `meows.familiar` (was `meows.kit` until 2.22.0; settings, activation and history
follow the rename). Plain name **Tabletop pictures**.

## The bench

Not everything is a one-shot. The first entry in the list, **Bench**, is a standing kit under
`Oneshots\Bench` that is always there: add a portrait, cut it to a token; add a map, grid it or
frame it; the results sit in the bench's folders, *Open the folder* shows them, and nothing on
the bench is written for a VTT. It is the same tab and the same verbs with the one-shot part
left off. When a bench picture turns out to belong to an evening, add it to that kit from the
bench folder.

## A kit is a folder

`Oneshots\<name>\` with `maps`, `tokens`, `handouts` and `notes`, and a `kit.json` for what a
file name cannot hold: the grid, the order the evening reaches things, captions. Delete the
manifest and it is still a folder of pictures. Every change on the tab is a change to a file;
the manifest only remembers. Anything Familiar alters keeps the picture it started from under
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
exports offset the grid by it. Five borders ship, parchment, stone, wood with iron corners, gilt
and torn paper, drawn as 9-slice tiles with an ornament in the corners.

## Tokens

The way the token makers on the web do it: a background disc if one is chosen, the picture cut
to the circle inside the ring, the ring drawn on top, and **everything outside the ring is
transparent**. Zoom and the two sliders move the picture inside the circle, with the preview
showing exactly what will be written. *Make the token* writes a 512 × 512 PNG over the file.
Sixteen rings ship: gold, silver, bronze, iron and blued steel; emerald, crimson, amber and
obsidian for friend, foe, neutral and boss; oak, ebony, rope, vine, studded iron, and rune stone
in two glows. Seven backgrounds: parchment, dark stone, night, ember, forest, arcane, sunburst,
for a portrait with a transparent or ugly background. *No frame* cuts the circle and nothing
else. *Side* is a word the Foundry module turns into the token's disposition.

## Your own frames

`Oneshots\Frames\` (beside the kits; *Open the frames folder* on the tab) is read on every
Refresh. `token-*.png` is a ring: square, transparent in the middle and outside, and the cut is
measured from the picture, so a ring drawn in any painting program works as it is.
`background-*.png` is a disc behind the portrait. `border-*.png` is a 9-slice tile, the slice a
third of its width. A `frames.json` there in the shipped format sets labels, inner radii and
slices exactly. A file named like a shipped frame replaces it. Nothing needs rebuilding.

## Handouts

Pictures the players get to see, framed anywhere they like, with a caption the VTT shows.

## The run sheet

The second view of the middle column, beside the pictures. **Notes** are markdown files under
`notes`: make one from the box, type in it, and it is saved when you switch notes or kits, press
*Save*, or close the tab. Each becomes a journal entry in Foundry. **Fights** are entries in
`kit.json`: a name, the map it is on, who is in it, and notes for the GM. *Who is in it* is
typed a line per group and read leniently, `3 Goblin`, `Goblin x3`, `Goblin (3)`, `goblins`; a
line that matches a token by name becomes a group the VTT places, any other line travels as
text, because "the barkeep hides behind the counter" is part of the roster too. Ctrl+K finds a
fight by name or by who is in it.

## Where it goes: Foundry

*Write for Foundry* writes a folder named after the kit under the exports folder: every picture,
and a `kit.json` with the numbers worked out, one scene per map with its size in pixels and in
squares, its grid or the fact that it has none, its offset, the order. The **meows-kit** module
in [Zera's VTT Forge Tools](https://github.com/Zeralius/Zeras-vtt-forge-tools) reads that folder
from inside Foundry: upload it through Foundry's file picker, press *Import a kit*, and a Folder
named after the kit appears holding one Scene per map, gridded or gridless as the card said, one
JournalEntry per handout and per note, and one Actor per token with its picture and disposition.
The run sheet lands as one journal entry per scene with a page per fight, linked from the scene
so its notes button opens it, and each fight's tokens are placed on the scene hidden, in a row
at the top-left, ready to be dragged where they go. Five maps in the kit, five scenes in a
folder, in order.

There is no way to write into a running Foundry from outside, and writing its database while it
runs corrupts the world, which is why the module exists.

**Getting the folder to the server is Meows' job once a server is set on the Settings tab.** A
Foundry export then goes there too, as background work with a file count on the Tasks panel,
into `Data/modules/meows-kit/kits/<kit>` under the server's folder, which is the module's kits
folder when that folder is Foundry's user data; the path is editable under the Foundry button
for a server laid out differently. The local export stays either way, so a server that is down
costs a *Try again* on the notification rather than the export. Without a server set, the folder
is uploaded through Foundry's file picker as before.

## Where it goes: Roll20

Roll20 has no way in: no API creates a page or uploads a picture, and the Pro-only Mod API runs
inside Roll20 and can do neither. So *Write for Roll20* does what Scruff does for the sites it
cannot post to: hands the files over spelled Roll20's way. Roll20 measures pages in 70-pixel
units, so each gridded map is resampled to exactly `columns × 70` by `rows × 70` and snaps to the
page grid on upload; a gridless map keeps its pixels and the sheet gives the page size in units.
Maps are JPEG under the per-file limit on the card (it moves with your plan, so it is a setting),
numbered in the evening's order; tokens are PNG; handouts JPEG. `ROLL20.md` beside them says
which page gets which size and which file, that the library folder is made by hand, and carries
the run sheet: each fight, its map, and how many of which token to drag onto the page.

## What it is not

A table. Running the session is the VTT's job; Familiar stops at its door. And nothing here draws on
a map: a frame goes outside the playable area, always, or the artist's grid would be lost.
