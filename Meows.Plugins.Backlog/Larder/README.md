# Larder

*Where the food is kept, not where it is eaten.*

Every installed Steam game in one list: its size, when it was last played, and which library it
sits in, with the never-touched largest first. The question it exists for is all of them at once.
One game folder is already answered by Chonk, which recognises a Steam game from its manifest;
three hundred never-launched games across five drives are not.

Plugin id `meows.larder`.

## Where it reads from

Steam writes everything needed beside each game in `appmanifest_<appid>.acf`: the name, the size
on disk, when it was last played and last updated. The libraries come from Steam's own
`libraryfolders.vdf`, with the client's own library first. No login, no network, and no game
folder is walked, so reading 800 games is reading 800 small text files. A library on a drive
that is not plugged in today is listed as not there rather than as empty, and one game in two
libraries is counted once.

A library Steam does not know about, or a Steam the registry does not point at, can be added by
hand on the right. Picking the `steamapps` folder, or a game's folder under it, is taken to mean
the library it belongs to.

## Never played is not the same as unknown

Steam writes `LastPlayed` as 0 for a game that has never been launched, and leaves the key out
altogether for some. The first is **never launched**; the second is **no record**, shown and
counted on its own line. Folding the two together would make the headline number a lie, and on
the machine this was written for that is 117 games out of 739.

## How it is ordered

*Never touched, largest first* is the default: the never-launched games by size, then the ones
with no record, then everything else from the longest since played. *Largest first*, *longest
since played* and *by name* are the other orders, and the list can be narrowed to never launched,
not played in a year, or no record. The right column gives each library its count, its size, what
its never-launched games hold, and what the drive has left. Home's card carries the never-launched
total, and Ctrl+K finds a game by name.

## What it refuses to do

It never deletes. A game folder removed behind Steam's back leaves Steam believing the game is
still installed, and the next update or verify puts it back. **Uninstall in Steam** hands the game
to Steam, which asks its own question and does the work; **Show folder** opens where it lives.
Moving a game to another drive is Steam's too (Properties, Installed Files, Move), for the same
reason.
