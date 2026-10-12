# Perch

*Where the cat sits and watches what is coming.*

One timeline answering a question that otherwise takes four separate mental calculations: what
actually posts tonight, and where. Every group of the posting bot merged into a single forward
view, this comic to Furry Paws at 21:00, that picture to Vore Unbirth at 22:30, out as far as the
queues last, with the moment each group runs dry marked in red.

Plugin id `meows.perch`. Under **Posting bot**, beside Kibble and Telegram Poster.

## Nothing here is new

The bot's rules were already written down and tested: `QueueRunway` knows the rate and the
stretching, `BotWorkspace` orders a queue the way `get_next_media` does, `MediaRules` reads a
comic's pages. Perch runs them forward instead of describing the present.

For each enabled group it does what `bot.py` does:

- an **interval** group fires at the bot's start plus `start_offset_minutes`, then every
  `interval_minutes`; a **daily** group fires at `hour:minute`, tomorrow if today's has passed
- each firing takes the next `files_per_post` files by modified time, oldest first unless
  `post_order` says otherwise
- after each post, a group with a **stretch** widens its gap towards the target from what is
  left, up to the cap, exactly as `retune` does
- jitter is shown as `+0–15 min` beside the time, because it only ever delays

When the queue runs out the row says so, **RUNS DRY**, and the group is not listed after that,
since everything from there is the bot repeating the archive. A group with nothing in the queue
and nothing in the archive is **NOTHING**, which is worse and looks different.

## The one thing it has to be told

Interval groups count from when the bot was started, and that clock lives on the machine running
the bot. Perch cannot see it. So the header has a box for it: type `2026-09-13 21:00`, or just
`21:00` for today, and interval groups phase from there, with the posts that have already gone
taken off the front of each queue. Leave it empty and the picture is how things would run if the
bot were started this minute, which is exactly right whenever it is about to be restarted.

Daily groups do not care either way.

## Reading it

Left, every group: how many files are queued, its schedule, its runway, whether it is being
stretched, and when it runs dry if that falls inside the view. Middle, the timeline by day. Right,
the picked slot at full size, with a comic's page count and how many batches of ten it becomes.

The horizon is 24 hours, 48, or a week.

## It is a projection, not a promise

A paused bot, a file the bot refuses at post time, a Kibble send that lands in a queue after the
refresh, a restart that moves every interval group's phase: any of these makes the future diverge
from the picture. Perch says so on the tab rather than implying a certainty it has not got, and
**Refresh** rereads everything.

## Settings

`%APPDATA%\Meows\plugins\meows.perch\settings.json` holds the bot folder if it was chosen by
hand, the typed start time, and the horizon. Perch never writes to the bot's folder.
