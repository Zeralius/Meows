# Birdwatch

Watches Bluesky accounts and drops their pictures into the intake folder Kibble sorts.

Plugin id `meows.birdwatch`.

## No account needed

Bluesky's public AppView answers `getAuthorFeed` for any public account without a login, and the
images come back as ordinary CDN URLs that fetch the same way. So Birdwatch holds no token, no app
password and no session, and there is nothing here for anyone to leak.

That is not a small convenience, it is why this plugin exists in the form it does. Meows
deliberately keeps secrets out of its own settings, which is why the bot token lives in the bot's
`.env` and Meows only reports whether one is there. A plugin needing its first real secret would
have been a decision to take on its own merits rather than as a side effect of wanting to see some
pictures. The public feed sidesteps it entirely.

The home timeline, the one assembled from everyone you follow, does need a login. That is the
natural next step and the point at which the credential question has to be answered properly.

## What it does

Give it an account, press **Watch**, press **Refresh**. Every watched account is read and the
pictures are merged into one grid, newest first, regardless of who posted them.

**Paste whatever you have.** A handle, a handle with the at sign, a profile link out of the
address bar, or the link the share button gives you, which points at one particular post. They
all name the same account in the same place, so they all end up watching it:

```
zeralius.bsky.social
@zeralius.bsky.social
https://bsky.app/profile/zeralius.bsky.social
https://bsky.app/profile/zeralius.bsky.social/post/3ktabcdefgh
```

A `did:` works too and is left exactly as written, since it is an identifier rather than a name.

Clicking a tile shows it full size along with the poster's own description and any content labels
the service put on it. **Save to intake** writes it to the intake folder; **Save all from this
post** takes the whole set, which is what you want when the pictures only make sense together.

**Read further back** goes further into each account's history, a batch at a time. Every account
keeps its own place, so accounts that post at wildly different rates stay in step, and one that
has run out is not asked again.

It spends what is already in hand before spending a request. Each account fetches fifty posts at
a time into a grid that starts at sixty pictures, so with more than one account most presses need
no network at all.

## Where the files go

The intake folder, which defaults to the same `Pictures\Kibble intake` that Saucer writes to. That
is the point of ending there: everything after "a file is in the intake folder" already exists, so
a picture saved here is sorted into a group queue by Kibble in the same pass as everything else.

Files are named `handle_date_postkey.ext`, so a folder of them reads at a glance a month later.
The service's own names are content hashes and tell you nothing.

**The extension comes from the response, not the address.** Bluesky's CDN serves a URL ending in a
bare content hash and hands back WebP. Guessing `.jpg` from the look of the link would write WebP
bytes into a file called `.jpg`, and the bot would then post a file whose name disagrees with
itself. Every type Birdwatch names is one `MediaRules` accepts, and there is a test that says so.

Because the name comes from the post rather than the response, whether a picture is already saved
can be answered before fetching it. Saving twice costs nothing and the tile just says `SAVED`.

## Video

Shown with its thumbnail and marked, but not saveable. Bluesky serves video as an HLS playlist
rather than a file, so pulling one down means reassembling segments, which is ffmpeg's job rather
than this plugin's. A button that cannot work is worse than no button.

## Quote posts

Followed one level. Quoting an image post is how a good deal of art travels and the pictures are
just as fetchable from there. One level only: a quote of a quote is somebody else's thread rather
than anything the watched account chose to show.

A link preview's thumbnail is deliberately ignored. It belongs to the site being linked, not to
whoever posted the link.

## Reposts

Marked with a badge and hidden by default, since a feed of reposts is a feed of other people's
material. **Include reposts** turns them back on.

## What it is not

A scraper. It reads the same public pages a browser would, one page at a time, when you ask it to,
and it saves what you click. There is deliberately no "download everything this account has ever
posted": the rate limits would object, and so would anyone on the other end.

## Other services

`IFeedSource` exists so a second one can be added without the grid learning about it. Mastodon fits
the same shape, a per-instance token and one endpoint, and would be next.

X does not, and the reason is worth writing down rather than rediscovering. Reading a timeline
needs a paid tier; the free one will not serve one at any useful volume. Working around that means
fighting login walls and rate limits that change without notice, so the feature would break
repeatedly and always at the worst moment. A plugin that says X is unsupported is better than one
that supports it on Tuesdays.

## Settings

`%APPDATA%\Meows\plugins\meows.birdwatch\settings.json` holds the watched handles, the intake
folder, whether reposts are shown, and `batch`, which is how many pictures the grid grows by each
time you read further back. No credentials, because there are none.
