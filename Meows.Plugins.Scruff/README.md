# Scruff

*Takes it by the scruff and tidies it up.*

Pictures go in, the metadata comes out, and what is left goes where you say: posted to Bluesky
and Mastodon from the tab, or handed to FurAffinity, X, Instagram and Reddit with the files in a
folder and every field on a sheet with a copy button beside it.

Plugin id `meows.scruff`.

## Why the metadata matters

A JPEG from a phone carries the phone's model, the time, the lens, and quite often the place it
was taken to a few metres. A PNG saved from an editor carries the editor's name and sometimes the
document title. None of that is visible, all of it goes out with the file, and nothing else in the
chain from intake to post takes it out.

Drop a file on the tab and the tile says what it carries. **GPS** gets a red badge of its own,
because it is the one that matters most.

Most of the big sites strip metadata themselves on upload. Some do not, the ones that do have
changed their minds before, and a file that is clean before it leaves the machine does not depend
on anyone's policy.

## Cleaning

The metadata is cut out around the compressed picture rather than the picture being decoded and
saved again. A cleaned JPEG has exactly the pixels it had before, byte for byte. The same holds
for PNG chunks and WebP chunks. What goes: EXIF, XMP, IPTC and Photoshop blocks, comments, text
chunks, timestamps, embedded thumbnails and vendor blocks. What stays: the colour profile, since
removing it changes how the picture looks, and Adobe's APP14 block, without which some files
decode with their colours inverted.

The one case that cannot be lossless is a picture stored on its side. A phone photo usually is,
with an EXIF tag saying which way up to show it. Take the tag away and the picture lies down. So
those are decoded, turned, and written out again at high quality, and the tile says **TURNED** so
you know which ones were.

**Clean into folder** writes a cleaned copy of everything into the output folder, which is
`Pictures\Scruffed` unless you move it. **Clean in place** replaces each original with its cleaned
copy, sending the original to the Recycle Bin first, which is the one to use on Kibble's intake
folder before anything is queued.

GIFs are left alone. What they can carry is a comment block almost nothing writes, and taking an
animation apart to look for one is not worth the risk.

## Posting

Write the post once. A title, the text, the tags, and a rating. Each place gets its own version:

| | Title | Text | Tags | Rating |
|---|---|---|---|---|
| **Bluesky** | first line | body | `#Hashtags`, as facets | self label: `sexual` or `porn` |
| **Mastodon** | first line | body | `#Hashtags` | the sensitive flag |
| **FurAffinity** | title box | description box | `under_scored keywords` | a reminder on the sheet |
| **X** | first line | body | `#Hashtags` | a reminder on the sheet |
| **Instagram** | first line | caption | `#Hashtags`, thirty at most | adult is refused outright |
| **Reddit** | the title | left off an image post | – | a reminder to mark it NSFW |

Tags are comma separated, or space separated when none of them have spaces. "big cat" becomes
`#BigCat` where hashtags are used, because a space ends a hashtag and a capital at each word is
what screen readers can still read, and `big_cat` on FurAffinity, because that is how its search
spells them.

Every ticked place shows what it would be sent as you type, with a count against its limit and,
when something is in the way, why. Too many pictures for Bluesky is said rather than quietly cut
to four. A file over a place's byte limit is a note, not a problem, because fitting will shrink
it; a GIF over the limit is a problem, because shrinking one would turn it into a still.

**Post** cleans everything first, then fits each file to each place separately: a format it
takes, no longer than its longest side, under its byte limit, easing the JPEG quality before
making the picture smaller. Bluesky gets files under a megabyte at 2000 pixels; Mastodon gets
them nearly untouched. Each place is tried whether or not the one before it worked.

### The places that post themselves

**Bluesky** wants a handle and an **app password**, made under Settings, Privacy and security,
App passwords. Never the account password: an app password can be revoked from that page without
touching anything here. Hashtags on Bluesky are not text, they are byte ranges in the UTF-8 of the
post pointing at a tag, so they are worked out from the composed body rather than typed. Alt text
goes on each picture. The rating becomes a self label, which is what makes the picture blur for
people who have asked for that.

**Mastodon** wants the server and a personal access token from Preferences, Development, with
write scope. The tab reads how long a post may be on that server when you sign in, since some
raise it well past 500. Each picture is uploaded, waited for if the server is still processing
it, and attached with its alt text. The visibility is one dropdown, and every post carries an
idempotency key, so a retry after a timeout cannot post twice.

Signing in checks the credential against the service there and then, so a typo is found at the
moment it is typed rather than at the first post.

### The places that do not

FurAffinity has no API at all. Instagram's needs a business account tied to a Facebook page and
a picture already on a public web server. X takes posts through its API only from a registered
developer app with signed requests, under terms that have changed three times in as many years.
Reddit's works for personal use but wants an app registration and a two step upload, and is the
one most likely to be done properly later.

So those are **handed off**. The fitted files land in a dated folder under the output folder, the
folder opens beside the browser, the browser opens on the upload page, and the card shows a sheet:
every field the site has, spelled the way that site spells it, with a **Copy** button beside each,
and reminders for what has to be clicked rather than pasted, such as the rating. X's compose box
opens with the text already in it. What is left is choosing the file and pasting.

It is not posting. It is everything except the last click, without the typing and without the
metadata.

## Where the credentials live

This is the first plugin that holds a real credential itself, and the rules were settled before
it was written: never in `settings.json`, never in clear text, and never an account password when
the service offers something revocable instead.

Each credential is a file of its own under `%APPDATA%\Meows\plugins\meows.scruff\secrets\`,
sealed with Windows data protection for the current user. It opens on this machine, for this
account, and is noise anywhere else. **Forget** on the card deletes it. Only the account name is
kept in the ordinary settings, because it is shown on the card and is not a secret.

## What it does not do

- **It does not post video.** Every one of these places treats video differently and several of
  them need it transcoded; that is a plugin of its own, and ffmpeg's job rather than Skia's.
- **It does not schedule.** Post is now. The queue and the clock belong to the Telegram bot and
  to Kibble, which feeds it.
- **It does not read HEIC.** Skia cannot open one without a codec that does not ship here, so a
  HEIC file is stripped of nothing and refused by every place. Export it as JPEG first.
- **It does not count like X counts.** A link is always 23 to X and some scripts count double;
  the 280 here is graphemes, which is close and occasionally a few out.
- **It does not remember drafts.** The boxes clear with the app. A post is written and sent, not
  kept.
