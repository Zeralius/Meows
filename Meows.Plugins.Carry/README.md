# Carry

*Moves the kittens to a better nest.*

Every other plugin answers a full drive by deleting. Carry answers it by moving: pick a folder,
pick a drive with room, and it copies, checks every file, removes the original and leaves a
directory junction where it was, so every path that pointed at the old place still works. It is
for the 200 GB that is worth keeping but does not need to be on the fastest drive.

Plugin id `meows.carry`. Chonk hands a folder over with *Carry to another drive*.

## Where it goes

Under `Carried` on the drive picked, with the folder's own path below it:
`C:\Users\me\Videos` becomes `D:\Carried\C\Users\me\Videos`. Nothing clashes, and the
destination says where each thing came from. Only fixed drives are offered: a USB drive, a
network share or a disc will not be there every time the computer starts, and a junction to one
would lead nowhere.

## What it refuses, before anything is written

A drive's root, Windows, Program Files, ProgramData, AppData, Users and a profile itself (a
folder inside any of them is fine); a folder that is already a junction; the drive it is already
on; a destination inside the folder or the other way round; something already where the copy
would go; not enough room, with 5% to spare; OneDrive files that live only in the cloud, which a
copy would download and a junction under OneDrive would break; a junction or link inside the
folder, which a copy cannot carry faithfully; and anything that cannot be read.

## How a carry goes

It asks first, with the size and where it will go, and says plainly that not every backup tool
follows a junction. Then:

1. The copy goes into a folder with a temporary name. Every file is hashed as it is read and the
   copy is read back and compared; dates, attributes and empty folders come too.
2. The original is measured again. If it changed while it was being copied, the copy is deleted
   and nothing else has happened.
3. The copy takes its real name, and the original is renamed aside. That rename fails when
   something has a file in it open, which answers "is it in use" without guessing.
4. The junction is made (`mklink /J`, which needs no administrator) and checked: it has to lead
   to the copy and show every file.
5. Only then is the original deleted.

Any step failing puts everything back as it was. If even that fails, nothing is deleted and the
message names where the original is. Stopping part way removes the unfinished copy.

## Carried, and the way back

The right column lists what was carried, with whether each junction still leads to its copy.
A junction whose drive or copy is gone is Home's trouble line, because every path that used it is
now broken. *Bring it back* does the carry in reverse: copied home beside the junction, checked,
the junction removed and the copy given its name, and only then the carried copy deleted. Each
carry and each return is a line in the history.

## What it is not

Not for Steam games: Steam moves its own (Properties, Installed Files, Move) and would not know
about a junction. Not a sync or a backup: there is one copy afterwards, on the other drive.
