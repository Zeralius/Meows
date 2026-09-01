# Mouser

Hunts down dead weight. Empty folders, empty files, shortcuts pointing at things that are gone,
and the leftovers a file browser scatters about.

None of it is large, which is exactly why nothing else finds it. Chonk answers what is big and
Purrge answers what is duplicated. This is the third question, what is simply pointless, and the
answer accumulates for years because no tool that sorts by size will ever put it near the top.

## What it looks for

| | |
|---|---|
| **Empty folders** | Nothing inside, at any depth. A folder holding only empty folders counts too |
| **Empty files** | Zero bytes, so there is nothing in them to lose, minus the ones that are empty on purpose |
| **Broken shortcuts** | `.lnk` files whose target is no longer on the disk |
| **Leftovers** | `Thumbs.db`, `ehthumbs.db` and `.DS_Store`, all rebuilt whenever they are wanted again |

Pick a folder, and the buckets on the left filter what the list shows. Select anything, one or
many, and send it to the Recycle Bin. It asks first, unless you tell it not to.

Removing an empty folder can make its parent empty, so the sweep runs again after every delete
rather than leaving a list on screen that is no longer true.

## Files that are empty on purpose

Plenty of zero byte files are doing exactly what they are for, and size alone cannot tell them
apart from dead ones. An empty `__init__.py` is what makes a Python package a package. A
`.gitkeep` exists only so git carries the folder around it. Unity writes thousands of `.mvfrm`
markers into a project where the file existing is the whole message.

So these are passed over:

- `__init__.py`, `__init__.pyi`, `py.typed`, `.gitkeep`, `.keep`, `.placeholder`, `.nojekyll`,
  `.metadata_never_index`, `.localized`, `.empty`
- anything ending `.mvfrm`, `.ModuleCompilationTrigger`, `.lock` or `.stamp`
- anything with no extension at all, which is nearly always a program leaving itself a note:
  `REQUESTED`, `WEBGL_SUPPORTED`, `CodeSignature`

Passing over them costs nothing, because a zero byte file takes no room worth reclaiming. The
whole value of this category is tidiness, so under reporting is free and over reporting is not.

On one real drive that rule took empty files from 12,219 down to 2,057, and what it removed was
6,266 Unity markers, 461 `__init__.py` and 30 `.gitkeep`.

Empty files are still the one category worth reading before you tick it. The other three are
answers; this one is a suggestion.

## Stopping keeps what it found

A sweep of a big folder takes a while, and **Stop** hands back what turned up before then rather
than throwing it away. The list, the buckets and the counts all fill in as far as the walk got,
and everything in them is safe to act on.

With one exception, which is why stopping says so on the tab. A folder cannot be called empty
until everything below it has been read, and a folder whose contents have not been reached yet
looks exactly like an empty one. So any folder the sweep had not finished, and every folder above
it, is held back rather than guessed at. Individual files are unaffected: each one is judged on
its own and does not depend on the walk finishing.

Run it again to see the rest.

## Only the topmost empty folder

A chain of empty folders is offered as one entry, the outermost. Removing it takes the rest, so
listing all of them would be noise, and every delete after the first would be aimed at something
already gone.

## Reading shortcuts

The target is read out of the `.lnk` bytes rather than through the Windows shell. Asking the
shell to resolve a shortcut makes it go looking: it hunts for a moved target, hits the network,
and can sit there for seconds on a dead drive. A scan touching thousands of shortcuts cannot
afford that, and a shortcut that needs hunting for is precisely the one being asked about.

A shortcut whose target cannot be read is left alone rather than reported. Not knowing where
something points is not the same as knowing it points at nothing, and only one of those is a safe
reason to delete a file.

That is not a rare case. Of the 302 shortcuts on the machine this was built on, 229 record their
target in a form this reads and 73 do not, mostly Start Menu entries pointing into the shell
namespace rather than at a file. Nothing is said about those 73. Of the 229, six came back broken,
and all six were confirmed gone by hand.

## What it will not touch

- The folder you pointed it at, however empty it is
- Anything inside a folder it did not walk into, which includes `node_modules`, `.git`, `obj`,
  `bin` and the Windows system folders while **Skip system folders** is on. A folder holding only
  one of those is never called empty, because nothing was learned about what is inside it
- Junctions and symlinks, which is what stops a walk going round in circles
- Anything it could not read, which counts as occupied rather than as empty

Everything goes to the Recycle Bin, so any of it can be brought back.

## Settings

Stored in `%APPDATA%\Mews\plugins\mews.mouser\settings.json`: the folder, whether system folders
are skipped, and whether it asks before deleting.
