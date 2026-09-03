# Telegram Poster

A Meows plugin for [telegram-posting-bot](https://github.com/Zeralius/telegram-posting-bot).
Browse its groups and queues, see what goes out next, edit group settings, and run it.

The bot is public, so the setup panel prefills its URL. You can clone it from inside Meows
without having the link to hand.

**It controls the bot, it does not contain one.** All the posting logic stays in `bot.py`:
comic batching, resume state, jitter, retries. This plugin reads `config.json` and the group
folders live off disk, and starts the bot as `python -u bot.py`. Nothing about the bot is
embedded here.

Plugin id `meows.telegram-poster`. Needs **Python**, and **git** for the clone step.

## The three columns

| | |
|---|---|
| **Left** | Groups, with queue and archive counts, a schedule summary, a warning glyph, and a checkbox controlling whether the bot schedules the group at all |
| **Middle** | Thumbnail grid for the selected group, switchable between `To_Send` and `Already_Sent`. Comics show their first page as a cover and carry a `COMIC` badge |
| **Right** | **Next up** when nothing is selected, **Selected** when something is, then that group's issues, then its settings |

Below the header sit three strips that only appear when they apply: missing Python or git, group
issues, and a token prompt.

## First run

If no bot folder is found, the tab shows a setup panel instead of the browser.

1. **Clone.** Repository URL and destination, defaulting to a sibling of the Meows checkout.
   `git` progress streams into the shared log. A private repo authenticates with your existing
   git credentials, which the plugin never sees. `GIT_TERMINAL_PROMPT=0` is set so a missing
   credential fails straight away instead of hanging on a prompt nothing can answer.
2. **Dependencies.** Runs `python -m pip install -r requirements.txt`. The status comes from
   actually trying `python -c "import aiogram, apscheduler, dotenv"` rather than guessing.
3. **Token.** Writes `BOT_TOKEN` into `.env`, replacing an existing line in place and leaving
   other lines alone. `.env` is gitignored and never arrives with a clone, so this step is
   always needed on a new machine. The field is masked, cleared right after writing, and never
   logged or read back. Only *whether* a token exists is ever shown.

The URL is prefilled with the public bot repository and saved once you change it, so pointing at
a fork or a private mirror is a one-time edit.

Already have a clone? Use **Bot folder...** in the header instead.

### Finding the bot

`BotWorkspace.Probe` resolves the folder at runtime. The saved path first, otherwise a
`telegram-posting-bot` folder containing `bot.py` in any parent of the executable or the working
directory. With Meows and the bot as sibling checkouts, that just works.

### Tool detection

Python and git are probed on activation and the result goes into the log. A missing tool raises
a notification in the shell, and switches off only the steps that need it.

On Windows, `python3` is often an App Execution Alias that prints a *localised* "Python was not
found" and exits 9009. That message still contains the word "Python", so detection matches a
version number like `Python 3.10.6` rather than the word. Otherwise the stub would be mistaken
for a working interpreter.

## Next up

`ResolveNextUp` restates the bot's `get_next_media`: `To_Send` ordered by **mtime**, honouring
`post_order` and `files_per_post`. Where there is no honest answer it says so instead of
guessing.

| Situation | What you see |
|---|---|
| `oldest` or `newest` | The actual file, with a full preview |
| `random` | "the bot draws its file at post time", because there is nothing to preview |
| `To_Send` empty | "will re-post a random file from `Already_Sent`", which the bot leaves in place |
| Nothing anywhere | Reported as a group issue instead |

Because the order is by mtime, copying or moving files rewrites it. Anything that disagrees with
the bot here is a bug in this plugin, not a quirk.

Interval groups count from the bot's own start time, which Meows cannot know, so the left column
says "every 60 min from the bot's start" rather than inventing a clock time. Daily groups do show
their next slot.

## Enabling groups

The checkbox writes `"enabled": false` into `config.json` and `bot.py` skips those groups when
building its schedule. The key is written only when a group is disabled, since absent already
means enabled. Re-enabling removes it rather than writing `true`, which keeps diffs small.

Folders are still created for disabled groups, so re-enabling needs nothing else.

## Warnings

Validation runs on load, on every edit, and after a refresh. Editing one group's folder can
create or clear a clash for another, so the whole set is re-checked each time.

Severity follows what the bot actually does. `name`, `chat_id` and `folder` are read with bracket
access, so a missing one raises `KeyError` at **startup** and takes down the whole bot, not just
that group.

| | |
|---|---|
| **Error** | `name`, `chat_id` or `folder` blank; chat ID still an unfilled placeholder like `REPLACE_WITH_CHAT_ID`, which is what a fresh clone ships |
| **Warning** | Folder does not exist yet; no `To_Send` subfolder; chat ID neither numeric nor `@name`; chat ID positive, which is a private chat; duplicate chat ID; shared folder; nothing in either folder; `To_Send` empty while the archive is not, so the bot is repeating itself |

A duplicate chat ID is worse than it looks. Jobs are keyed by chat ID with
`replace_existing=True`, so only the **last** such group is scheduled and the earlier one
silently never posts.

`bot.py` does the same required-key check itself at startup and skips unusable groups instead of
crashing, so hand-editing `config.json` outside Meows is covered too.

## Stretching a short queue

Left alone, a group posts at its configured rate until `To_Send` is empty and then starts
repeating its archive. The channel looks busy while nothing new is going out, which is the
failure you do not notice.

**When the queue runs short** turns that into a slowdown instead. Give it how long what is left
should last and how far apart the posts may get, and the bot widens its own interval as the queue
runs down and puts it back as soon as you feed it.

The line under the two fields is what the bot is doing right now, not what the file says. The two
differ the moment a queue runs short, which is the whole point of the feature:

> Posting every 12 h instead, which makes what is left run for 7 days.

Some queues cannot be stretched into health, only slowed down, and it says so rather than
pretending:

> Posting every 24 h, the slowest allowed, which gets it to 5 days.

The stretching itself is `bot.py`'s work. Only the bot knows the queue at the moment it posts and
only it can retime a job that is already running, so this tab writes the setting and shows the
result. The sum is duplicated in `QueueRunway` so the pace on screen is the pace the bot will
actually run at, and a test pins the two against the same table.

Runway everywhere else, including Kibble's group list, is the stretched figure for the same
reason: it is the honest answer to when the group runs out.

## Editing settings

The right column edits `name`, `chat_id`, `folder`, the schedule (interval or daily), jitter,
`files_per_post`, `post_order`, `comic_order` and the stretch. Edits stay local until you press
**Save config.json**, so a stray keystroke never reaches a running bot. Saving rewrites the whole
file, so every group is written, not just the edited one.

Because it rewrites the whole file, anything in `config.json` that this does not edit is carried
across untouched rather than rebuilt. `start_offset_minutes` is the reason: it spaces groups so
they do not all fire on the hour, nothing here changes it, and saving used to drop it. Keys the
bot gains later are kept the same way without needing a field here.

The bot reads its config only at startup, so restart it for a changed schedule to take effect.
The stretch is the exception: the bot adjusts that itself while it runs.

## Settings

`%APPDATA%\Meows\plugins\meows.telegram-poster\settings.json` holds the bot folder path, the
detected Python command, and the repository URL. No secrets. The bot token lives only in the
bot's own `.env`.
