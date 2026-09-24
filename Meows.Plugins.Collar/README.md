# Collar

*The tag with the details on it.*

The dates that matter, and the paper behind them. A warranty ending, insurance renewing, the TÜV,
the domain, the filter that should have been changed in March.

Plugin id `meows.collar`.

## Why this one raises notifications

Every other plugin here answers a question about files, and a file is still there tomorrow.
Collar is about time, so what it knows is true whether or not its tab is open — which is exactly
what the shell's notification surface is for.

A date that has passed, or one coming up inside the lead time, is raised as a **condition**: one
entry, replaced rather than stacked when it is checked again, and taken back down the moment
nothing is due. Conditions cannot be dismissed by hand, on purpose, because only the plugin knows
whether one still holds.

The calendar is also looked at every six hours while the tab is switched on. Nothing about that is
expensive; the point is that a tab left open across midnight would otherwise still be showing
yesterday's arithmetic and would never raise the thing that fell due while it sat there.

Since 2.0 Meows keeps running in the tray when the window is closed, so a date that falls due
during the day is raised while you are at the desk, with a dot on the tray icon. Nothing is raised
while you are logged out; Meows is a tray icon, not a service.

## Adding a date

**Drop a receipt on the tab.** The file is attached, the title comes from its name, and the date
is set two years from the file's own date, which is the statutory warranty here. Adjust whatever
is wrong and it is done. **From a file…** does the same through a picker, and adds the file to
whatever is selected if that entry has none yet.

A list that has to be typed into is a list nobody keeps up, which is why entering one costs a drag
rather than a form. **Add a date** is there for everything that never came with a receipt.

Each entry carries what it is, what sort it is, when, whether it comes round again, a note, and
optionally the file. The file is referenced where it already lives and never copied, so Collar
holds a path and nothing else.

## From another tab

A *files* handoff makes one entry per file the way a drop does, and the sender hears *2 added to
Collar*. If an entry is selected and has no paper yet, the first file attaches to it instead.

## Dealt with

One button. Something that **repeats** moves to its next date, and the walk forward keeps going
until the date is genuinely in the future, so a yearly thing left unopened for two years lands on
this year rather than on one already missed. It keeps the day of the month it was set on, because
that is what the insurer uses.

Something that happens **once** is finished with, and stays at the bottom of the list rather than
disappearing, because the record of when you did it is half of why the entry existed.

## The lead time

One number in the header: how many days ahead something starts being worth saying out loud.
Thirty by default.

Reminding too early is the failure mode nobody talks about. Something six months out is not news,
and a plugin that cries every launch stops being read.

## When a rule asks

On the Rules tab Collar offers **Put it on the list for today** and **Put it on the list for a
week from now**. Either makes a one-off entry named after the file or thing the other plugin's
event was about, with the file attached when there is one and that plugin's words as the note,
and leaves whatever is selected alone. The same thing asked for the same day twice is one entry.

## What it does not do

- **It does not sync anywhere.** The list is a few lines in Meows' own settings under
  `%APPDATA%\Meows`, and that is the entire storage story.
- **It does not read your files.** A dropped PDF is attached and dated, never opened or parsed.
  Mittens, if it is ever built, is the plugin that reads paperwork.
- **It does not confirm deletes.** A wrongly deleted date costs a retype, which is not the same
  kind of loss as a file, and a confirmation on every one of these would be noise.
