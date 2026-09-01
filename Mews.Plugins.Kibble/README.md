# Kibble

Feeds the posting bot. Open a folder of new material, and send each file to whichever group
needs it, one key press at a time.

Plugin id `mews.kibble`. It reads the same `telegram-posting-bot` checkout that
[Telegram Poster](../Mews.Plugins.TelegramPoster/README.md) uses, and the two share
`Mews.Bot.Core/` so their idea of the bot's rules cannot drift apart.

## The problem it solves

A group's queue length tells you almost nothing on its own. One group with 63 files queued and
another with 481 look very different until you notice the first posts hourly and the second
posts once a day. The first has under three days left. The second has more than a year.

So Kibble never leads with the count. It divides the queue by the group's own posting rate and
shows **days of runway**, then sorts the destination list driest first. The group that needs
feeding is always the one at the top, under your thumb.

That matters because a dry group is invisible from outside. The bot does not stop or error. It
quietly starts re-posting the archive, and nobody notices for a while.

## The three columns

| | |
|---|---|
| **Left** | Every group as a destination, driest first, each with a number key and its runway. Underneath, what a multi-file pick goes in as |
| **Middle** | The folder you opened, as a thumbnail grid. Ctrl and shift pick several. Sort it with the dropdown |
| **Right** | Full preview of the selected file, its size and date, and the queue order setting. Drag the divider and the picture grows with it |

## Sorting a folder

Press **1** to **9** to send the selection to that destination, or **space** to skip it.
Both number rows work, top and numpad. Past the ninth group you click instead. After a send the
selection moves forward through the grid, so a folder is sorted without ever reaching for the
mouse.

Those keys are listened for on the window rather than on whatever happens to have focus. That
matters more than it sounds: sending removes the tile that had focus, so a handler hanging off
focus works exactly once and then goes quiet until you click something. Typing in the comic name
box is still safe, since keys aimed at a text box or a dropdown are left alone.

Runway is colour coded, so the left column reads without being read: red for dry, amber under
three days, green above it. Under a day is reported in hours rather than as `0,8 days`.

**Files move, they do not copy.** **Undo** puts the last one back exactly where it came from.

## Sending several at once

Ctrl click to add files to the pick, shift click to take a run of them. Arrow keys and shift
arrow work too. With two or more picked, the buttons under the group list decide what a send
actually does:

| | |
|---|---|
| **One comic** | Zip the pick into a single `.cbz`, so it posts as one comic |
| **Separate files** | Move them in as they are, so each one posts on its own |

The choice is remembered, and the heading above the groups says which one is armed before you
commit to it.

**Separate files** is the plain bulk move. Files go in one by one in the order the grid is
showing, so the sort dropdown decides it. Each is checked on its own, which means a file the
group refuses is simply left behind while the rest go through, and the reason is shown for the
one that stayed. Anything the bot can post is fair game here, including gifs and pdfs that could
never be comic pages.

If you are queueing with **Date them as they are queued**, a batch would otherwise land on the
same timestamp and the bot orders its queue by exactly that, so the files are stamped a second
apart to hold the order you sent them in.

## Making a comic out of several files

With **One comic** armed, sending zips the pick into a single `.cbz` in the group's queue, so
they post as one comic instead of as a run of unrelated images.

Name the archive in the box on the right. **Name from** decides what it is filled with:

| | |
|---|---|
| **The folder name** | The folder you opened. Clashes get a number, so `set_2`, `set_3`. The default |
| **Words the files share** | Built from the picked file names themselves |
| **Folder name plus a random tag** | `bigfolder-k7m2`, so two picks never collide |

**Words the files share** counts how often each word turns up across the picked names, keeps the
ones in at least two files, and joins the commonest first. Picking `foxy_cafe_01`, `foxy_cafe_02`
and `foxy_diner_03` gives `foxy cafe`. Pure numbers are thrown away, because page numbers are
exactly what differs between the files rather than what they share, and a word repeated inside
one file only counts once so a single shouty name cannot outvote the rest. When the files share
nothing, which is what happens with hash named downloads, it falls back to the folder name rather
than inventing something out of the hashes.

The **random tag** settles when the pick first becomes a comic and then holds still while you add
more files, since a name that reshuffles under you is not one you can trust. Start a fresh pick
and you get a fresh tag.

Whatever the rule suggests, the box stays yours: type over it and that is the name. Anything a
file name cannot hold is dropped, and an empty box becomes `comic`.

**Every picked tile shows the page it will be**, as a number in its corner, so the order is
something you can see before the archive exists rather than something you discover afterwards.
The numbers are a comic idea only, so they do not appear in **Separate files** mode, where the
order is simply the order the grid is already showing. Two dropdown choices decide them:

| | |
|---|---|
| **Pages in file name order** | Natural order, so `page2` comes before `page10`. The default |
| **Pages in the order I picked them** | Ctrl click the files one at a time and that is the page order |

Pick order is the one to use when the file names carry no order at all, which is most of the time
for anything downloaded. Adding another file to the pick never renumbers the ones already in it.

Page order is the thing worth explaining. The bot has a per group `comic_order` of `name`, `date`
or `zip_order`, and Kibble does not control which one a group uses, so whichever order you chose,
the archive is written to satisfy all three at once:

- pages go in in the order you chose
- each entry gets an index prefix, `1_`, `2_`, `3_`, so sorting by `name` gives that same order
- entry times ascend, so sorting by `date` gives it too
- and `zip_order` is simply the order they were written

The original timestamps are put back if you undo, since the ones inside the archive are synthetic
and exist only to pin the order.

Only photos and videos can be pages, because that is all a Telegram media group takes. A gif, a
pdf or another archive in the pick is refused by name and nothing is moved. Send those as
**Separate files** instead, which is the whole reason that mode exists. The bot posts a long
comic in batches of ten pages, so there is no page limit to worry about here.

**Undo unpacks the comic** back into the files it was made from and deletes the archive. The
bytes come out of the archive itself rather than a copy kept aside, so an undo cannot hand back
something subtly different from what went in.

## Big folders

A folder of thousands is slow to open, because every file gets a tile and every tile gets a
thumbnail decoded whether you ever scroll to it or not. The grid uses a wrapping layout, which
does not virtualise, so nothing is free just because it is off screen.

**In batches**, next to the sort dropdown, builds only a batch at a time. Pick 100, 200, 500 or
1000. A **Load more** button under the grid pulls in the next batch and says how many are still
waiting, and the header says how much of the folder is showing.

The batch tops itself back up as you work: send ten files and ten more appear, so you keep a full
screen without ever loading the whole folder. Counts and sorting always cover the **whole**
folder, not just the batch, so "50 files left" means fifty and newest-first means newest of all
of them.

It is off by default, since it changes nothing for a folder of normal size. Turn it on for the
big ones and it is remembered.

## Sorting the folder

The dropdown above the grid orders what is waiting: **name A to Z**, **name Z to A**, **newest
first**, **oldest first**. Name order is natural, so `page2` comes before `page10` rather than
after it. The choice is remembered.

Sorting covers everything waiting, not only what is on screen, and it reuses the tiles that
survive the reorder, so thumbnails that have already decoded stay decoded. It does drop a
multi-file pick, because page numbers that refer to an order you can no longer see would be worse
than losing the pick.

## What it refuses to do

A refused file stays in the grid with the reason shown, rather than disappearing or being sent
anyway. Kibble refuses when:

- **the bot cannot post that type.** Grid tiles for those are badged *THE BOT SKIPS THIS* before
  you ever try. The folder listing shows everything rather than hiding unsupported files,
  because a file the bot cannot use is exactly what you want to see.
- **the group already has it.** Checked by content, not by name, against both `To_Send` and
  `Already_Sent`, so a renamed copy of something already posted is still caught. This is a check
  on single files. A comic you build here is a new archive, so there is nothing yet to match it
  against.
- **a comic archive has no pages in it.**

Dedupe is deliberately **per destination**. Two groups wanting the same picture is normal, so
the same file being queued elsewhere is not a reason to refuse it here.

A name clash never overwrites what is already queued. The incoming file gets a suffix.

## Queue order

The bot posts oldest modified time first, which makes the timestamp a scheduling decision rather
than metadata. The dropdown picks what happens to it on the way in:

| | |
|---|---|
| **Keep each file's own date** | Genuinely older art posts first. The default |
| **Date them as they are queued** | First in, first out, regardless of how old the file is |

## Notifications

Kibble raises a shell condition called `dry-groups` while any group has an empty queue, naming
them. It clears itself once they are fed. The **Alerts** badge in the status bar carries it.

## Finding the bot

Same probe as Telegram Poster: the saved folder if you have picked one, otherwise the usual
places near the Mews checkout. A folder counts only if it holds `bot.py` and `config.json`.
**Bot folder...** in the header sets it, and it is remembered per plugin.

Kibble reads `config.json` and writes nothing to it. Its own settings are the bot folder, the
last folder you opened, and the queue order choice.
