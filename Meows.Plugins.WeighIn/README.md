# Weigh-In

Measures every drive once a day and says what grew: which drive lost how much since last week,
and the folders responsible.

Plugin id `meows.weighin`. Plain name *Drive growth*.

## What it does

Chonk answers where the room on a drive went, right now. This answers what *grew*, which is the
more useful question, because nobody notices a drive filling until it is full.

**Reads.** Once a day, on the shell's clock, every ready fixed drive (or the roots listed in its
settings): the drive's own numbers from Windows, and a walk of the drive keeping folder totals
down to a fixed depth, two levels by default. That is the same walk Chonk does, but what is kept
is a few thousand numbers rather than every file, one small JSON file per day under the plugin's
data folder, and the last sixty are kept. The first reading is taken when the tab first opens;
after that it waits its day. Purr lists the schedule like any other.

**Shows.** Left, the drives: used of total, and the movement since the chosen window, a day, a
week, a month, three months back. The reading nearest that far back is the comparison; when there
is none that old the oldest there is stands in, and the card says which date. Middle, the headline
for the selected drive, *F lost 200 GB since 7 Sep; 8% free*, and under it the folders
responsible: the deepest folders that moved by more than the threshold, without the parents that
only moved because their children did, so the answer is `Steam\steamapps\common` once rather
than three times over. Right, the selected folder with *Measure it in Chonk*, which hands the
folder across, and *Open in Explorer*; and the dials, the threshold in megabytes and the depth.

**Changes.** Nothing on the drives. It writes its readings and reads them back. Each reading
leaves a line per drive in History, and a drive under ten percent free that is still shrinking
against last week is a standing condition on the notification surface, cleared the day it is not.

## Budgets

A line a folder is not meant to cross: Downloads at 20 GB, Recordings at 200 GB, the intake folder
at 5. **Give it a budget** on a folder selected in what grew, or **Budget another folder…** for
one that has not shown up there; the box on its row is the line in gigabytes, the same gigabytes
every size on the tab is shown in. A new budget starts at the folder's size today rounded up to
the next five, and is there to be changed.

A budgeted folder is measured at every reading at whatever depth it sits, out of the same walk,
so it costs nothing extra. Over its line, a warning stays up on the notification surface with
the number in it and Chonk one button away, the Home card turns red, and the history gets one
*over-budget* line the reading it crosses, so a rule can act on the crossing. The warning clears
the day the folder is under again, and changing a budget checks it at once. A folder meant to be
full, the games drive, simply is not given one: a budget is only ever set by hand.

## When a rule asks

On the Rules tab Weigh-In offers **Take a reading now**, the same pass as the button. Several
rules asking while a reading runs all get that one reading's answer, rather than a second walk
of every drive straight after the first. Each drive's reading is an event a rule can wait for.

## With no window

`Meows.exe --do weighin.measure` takes the same reading from Task Scheduler, saved, pruned and
journaled the same way, whether or not Meows is open. A folder over its budget comes up as a
Windows notification, since nobody is looking at the tab. While Windows has run it in the last
eight days, the tab's own schedule stands down, so the reading happens once.

## What it refuses to do

It does not walk on opening when a reading exists, and it does not walk more than once a day
unless *Read now* is pressed: six nearly full drives on a timer is not something to do without
thought. It keeps folder totals, never files, so its history cannot become the thing filling the
disk. And it does not delete anything; that is Chonk's job, one handoff away.
