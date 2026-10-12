# Catnip

Finds what was downloaded and never opened: sorted by when a file was last touched rather than
how big or how old it is, so it finds the things acquired in a burst of enthusiasm and never
looked at since.

Plugin id `meows.catnip`. Plain name *Never opened*.

## What it does

**Reads.** The folders on the left, Downloads by default, walked once on *Scan*: every file's
size and its three stamps, created, written, last opened, taken from directory metadata. Nothing
is opened, so the walk cannot disturb the very thing it reads. Windows keeps the last-access
stamp on every drive (throttled to about once an hour since Windows 10 1803), and nothing else
reads it; Purrge and Mouser open files and put the stamp back afterwards through
`Meows.Disk.AccessTime`, Chonk never opens one, so the history is true.

**Shows.** The files, least recently touched first, with the folder, the size, and either *never
opened, arrived 7 months ago* or *last opened 2 years ago*. A file arrived when it was written
or created, whichever is later, since a copy is created after it was written; *never opened*
means the last access is no later than an hour after that, because an open inside the hour a
file arrived cannot be told from the arrival. The headline: *1,204 files, 38 GB, not opened
since they arrived, untouched for 90 days or more; the oldest from Aug 2023*. Three dials: only
never opened (off shows everything untouched for the window, opened once or not), untouched for
at least so many days, and a size floor. The first 500 are listed; the dials narrow it.

**Changes.** Nothing, unless asked. *Open it* opens the file, which is the one honest way off
the list: the stamp moves and the row goes. *Show in Explorer* reveals it. Ctrl-click and
Shift-click pick several; *Recycle Bin* sends every picked row there in one go, a History line
each. *Ask Purrge for other copies* hands the picked files to Purrge, which walks the drives
they sit on opening only files of exactly their sizes, and the answer, *1 of 3 have another
copy*, lands on Catnip's status line with the sets listed in Purrge. A folder handed over from another tab is added
to the list and walked at once, and the sender hears how many were never opened.

## What it refuses to do

It does not walk on opening, and it does not sweep: a file never opened is a file that was
wanted once, and the point is to look at each, not to clear a number. Picking several is for
the twenty you have looked at. It does not read
inside files, ever, since reading one would be opening it.
