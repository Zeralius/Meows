# Molt

Sheds the caches and build output that can be rebuilt, and says what losing each one costs before
you do it.

Plugin id `mews.molt`. Windows only, because deletion goes through the shell's Recycle Bin.

## What it offers

One card per thing, biggest first, each saying four things: what it is, **what losing it costs**,
where it lives so the claim can be checked rather than trusted, and how much room it is taking.

| | |
|---|---|
| **Windows temp** | Only entries older than a week. Anything touched recently may still be in use |
| **NuGet download cache** | Packages NuGet kept after downloading |
| **NuGet global packages** | Every package version ever restored here. Costs a full re-download |
| **npm cache** | So npm does not fetch the same tarball twice |
| **pip cache** | Wheels pip kept after building or downloading |
| **Crash dumps** | Memory dumps from things that crashed |
| **bin and obj** | Compiler output under a projects folder you pick |
| **Unity Library and Temp** | What Unity imports every asset into. Reimported on next open |

Nothing empty is ever offered, so a card only appears when it has something to say.

**The cost line is the point.** "Safe to delete" is a claim, and a tool making that claim about
someone else's disk owes them the reasoning. None of the cost lines say "nothing", because that
is never quite true: the cheapest of them still costs a slower first install.

## What it leaves alone, and why

**Browser caches.** They are locked while the browser is running, so clearing them is unreliable
at best and half done at worst.

**The JetBrains folder.** It holds settings and installed plugins as well as caches, and telling
them apart reliably is more than this should be guessing at.

Both are deliberate. A tool that reclaims space by removing things it does not fully understand
is one bad guess away from being the problem.

## Recycle Bin or outright

This is the one place in Mews where the Recycle Bin is not automatically the right answer.
Everything else here goes to the bin because an automated judgement should stay reversible. But
**a bin is still on the disk**, so shedding forty gigabytes into it frees nothing at all until
the bin is emptied, which is the opposite of the point.

The way out is that a cache is defined by being rebuildable. Its safety does not come from the
bin, it comes from the tool that made it being willing to make it again.

So both are offered and the confirmation is honest about which guarantee you are getting. **The
bin is the default**, because wanting the room back this second is a decision someone should make
rather than have assumed for them.

Shedding permanently goes one item at a time, because a cache always has a handful of files
something still has open, and one locked file must not stop the other nine thousand.

## Speed

The caches are measured in about a second. The projects folder is the slow part, and a Unity
project is why: a single one can hold tens of thousands of folders under `Library` and `Temp`.
Those are skipped while hunting and collected whole instead, which turned a scan that had not
finished after two minutes into one that finishes. The hunt also stops at eight levels deep,
since build output lives near a project root and never twenty levels down.

It runs as background work, so switching tabs does not abandon it, and it says how many folders
it has searched as it goes.
