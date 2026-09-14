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

## What it refuses to do

It does not walk on opening when a reading exists, and it does not walk more than once a day
unless *Read now* is pressed: six nearly full drives on a timer is not something to do without
thought. It keeps folder totals, never files, so its history cannot become the thing filling the
disk. And it does not delete anything; that is Chonk's job, one handoff away.
