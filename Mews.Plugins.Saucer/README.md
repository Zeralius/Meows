# Saucer

Keeps what you copy, images included, and drops them into an intake folder Kibble can sort.

Plugin id `mews.saucer`. Windows only: it talks to the Windows clipboard directly.

## Getting an image out of a browser

Right click a picture, **Copy Image**, and it appears here. Turn on **Save images automatically**
and it also lands in the intake folder as a PNG without being asked. Point Kibble at that same
folder and the two meet: copy in the browser, sort in Kibble.

**A browser extension would not do this better, and mostly cannot.** An extension is sandboxed and
cannot write to an arbitrary folder on the disk. `chrome.downloads` can only put things under the
Downloads directory, and reaching anywhere else needs a separate native messaging host installed
and registered alongside it. The clipboard route needs nothing installed, works the same in
Firefox and Chrome, and is one right click either way.

The one thing an extension would add is saving without the picture passing through the clipboard,
which matters if you want to keep whatever you had copied. Worth doing only if that turns out to
be a real annoyance.

## What it keeps

The forty most recent clippings, newest first, images with a thumbnail and text with its first
line. Copying the same thing twice does not make two entries. **Pin** one and it is never pushed
out by newer ones.

**History lives in memory and is never written to disk.** A clipboard ends up holding a password
sooner or later, and the honest way to handle that is not to store it at all. Saving an image is
the only thing here that writes anything, and closing Mews forgets the lot.

Images are always saved as **PNG**, even though the clipboard hands over an uncompressed bitmap.
A 400 by 260 clipboard bitmap is about 416 KB of raw pixels and lands as roughly 1 KB.

## How it watches

By asking Windows for the clipboard's sequence number a couple of times a second. That number
changes whenever anything is copied and costs nothing to read, so there is no hidden window and no
hook, and reading the clipboard itself only happens when something has actually changed.

Untick **Watch the clipboard** and it stops. Nothing is kept while it is off.

## Why the Win32 calls

The clipboard is read and written through `user32` directly rather than through the toolkit,
because image support is the first thing to be patchy in a cross platform clipboard abstraction
and an image is the entire point here.

A clipboard bitmap arrives as a bare DIB: the fourteen byte file header that would make it a
`.bmp` is exactly what the clipboard leaves off. Putting one back on the front is the whole
conversion, and the awkward part is working out where the pixels start, since a palette or a set
of bitfield masks may sit between the header and them.

Browsers usually offer a real PNG on the clipboard alongside the bitmap, which is taken in
preference and skips the conversion entirely.
