# Portion

*Controls the serving size.*

Walks every queue of the posting bot for what will fail at the moment it is sent, and for what
will post but not the way it was meant to, before either happens at three in the morning. Then
shrinks the pictures that can be shrunk.

Plugin id `meows.portion`. Under **Posting bot**, beside Kibble, Perch and Telegram Poster.

## What the bot will fail on

The Bot API refuses a **photo** over 10 MB, or one whose width plus height passes 10000, or one
more than twenty times as wide as it is tall. It refuses a **video, gif or document** over 50 MB.
A **comic** is unpacked and its pages sent as photos, so each page is held to the photo limit;
a comic with no postable page at all is logged as an error and skipped, and a page whose bytes
are not a picture, an HTML error page saved with a `.jpg` name being the classic, fails the whole
batch it is in.

None of that is visible in a folder listing, and the bot does not check ahead. Portion does, and
Kibble now refuses an oversized file at the click for the same reason.

## What will post, but not as expected

Two things that are not failures but are worth knowing before rather than after. Files inside a
comic that are neither photo nor video are skipped by the bot without a word, so a `ComicInfo.xml`
or a stray `.txt` is listed as a **NOTE** with its count. And a comic long enough to become more
than three batches of ten goes out as that many separate posts, two seconds apart, which is not
what "one comic" usually means when it is queued.

This half was sketched in IDEAS.md as its own plugin, Hairball. It lives here because Portion
already has every comic open to weigh its pages, and a second plugin walking the same queues to
open the same archives would have been the same afternoon twice.

## Weighing is cheap, comics are not

Sizes come from the directory. A picture's dimensions come from its header, without decoding
it. Comics have to be opened, which is real work across a dozen queues, so the whole walk runs as
background work with a progress line and can be cancelled; the tab is usable while it runs.

## Shrinking

**Shrink this one** or **Shrink all that can be**. A photo is brought under the limits the way
Scruff fits a picture to a site: the quality is eased first, and the picture made smaller only if
that is not enough, so what changes is as little as possible. A comic is rebuilt with its heavy
pages fitted and everything else copied through byte for byte, same names, same order, so the
bot's resume state still means what it meant.

Never in place. The new file is written beside the original under a temporary name, checked to be
smaller and to decode, and only then does the original go to the Recycle Bin and the new one take
its name. An encoder that fails halfway and leaves a truncated file in a queue is worse than the
oversized file was.

**The modified time is carried over.** The bot orders a queue by it, and a shrink that stamped
"now" on the file would quietly send it to the back of the line.

## What it leaves alone, and says so

- A **video** over 50 MB needs re-encoding, which is ffmpeg's job. The row says so.
- A **PDF** over the limit has to be split by hand; there is no honest way to shrink one without
  knowing what is in it.
- A picture with an **odd ratio** needs cropping, which is a decision rather than a conversion.
- A comic with **no postable pages** or a **page that is not a picture** needs a person to open
  it. Portion will not guess which page to drop.

## Holding back

What Portion cannot shrink still cannot stay in the queue, or the bot will fail on it at night.
**Hold it back** moves the selected file into the group's `Held_Back` folder, a sibling of
`To_Send` that the bot never reads, and the row leaves the list. Nothing is deleted; the folder is
where decisions wait. Kibble puts files there at the click for the same reason, and Telegram
Poster's queue rows can do it too.

Portion also takes files handed over from another tab, Telegram Poster's queue rows in
particular: each is weighed on its own and listed if the bot would object, and the status line
says how many were fine as they were.

## Settings

`%APPDATA%\Meows\plugins\meows.portion\settings.json` holds the bot folder if it was chosen by
hand. Nothing else is remembered; every open of the tab weighs again.
