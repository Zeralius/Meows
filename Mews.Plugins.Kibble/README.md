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
| **Left** | Every group as a destination, driest first, each with a number key and its runway |
| **Middle** | The folder you opened, as a thumbnail grid |
| **Right** | Full preview of the selected file, its size and date, and the queue order setting |

## Sorting a folder

Press **1** to **9** to send the selected file to that destination, or **space** to skip it.
Both number rows work, top and numpad. Past the ninth group you click instead. After a send the
selection moves forward through the grid, so a folder is sorted without ever reaching for the
mouse.

Runway is colour coded, so the left column reads without being read: red for dry, amber under
three days, green above it. Under a day is reported in hours rather than as `0,8 days`.

**Files move, they do not copy.** **Undo** puts the last one back exactly where it came from.

## What it refuses to do

A refused file stays in the grid with the reason shown, rather than disappearing or being sent
anyway. Kibble refuses when:

- **the bot cannot post that type.** Grid tiles for those are badged *THE BOT SKIPS THIS* before
  you ever try. The folder listing shows everything rather than hiding unsupported files,
  because a file the bot cannot use is exactly what you want to see.
- **the group already has it.** Checked by content, not by name, against both `To_Send` and
  `Already_Sent`, so a renamed copy of something already posted is still caught.
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
