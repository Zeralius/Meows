# Bookshelf

Ebook library: dedupe by content, unfinished first, staging for the ereader.

Plugin id `meows.bookshelf`.

## What it does

Watches the folders ebooks live in — a Calibre library is looked for on first open, anything
else is added by hand — and lists what's there: author and title read from the file name
("Author - Title", as usually shelved), format, size, and when it was last opened, by the
filesystem's own stamp. The shelf never opens a book itself, and hashing puts the stamp back
afterwards, so a scan is not the books being used.

Identical bytes are named (the most recently opened stays the book, the rest are the copies),
and started-but-never-finished sorts least recently opened first. A status per book —
unread, reading, finished — survives rescans; states for files that are gone are forgotten
quietly. "Stage unfinished" copies the reading pile into a staging folder for the ereader,
leaving what's already there alone; nothing is ever moved. What goes goes through the
Recycle Bin. Every scan, finish, staging and recycle is a line in the History tab.

A rule can stage the unfinished (`bookshelf.stage-unfinished`).

## What it refuses to do

No metadata parsing (the file name is the catalogue), no covers, no store integration, and
no deleting outright: the Bin or nothing.
