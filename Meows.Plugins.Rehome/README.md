# Rehome

*Packs the cat up before the move.*

Packs the machine up before a clean Windows install: what is installed and how to get it back,
what the wipe takes and where to keep it, the not-folders and the keys, and the way back
afterwards.

Plugin id `meows.rehome`. Plain name *Reinstall*. Windows only: it reads the registry and asks
winget, netsh and PowerShell.

## What it does

**Reads.** On *Scan*, five things, none of them written anywhere yet:

- **Programs.** The uninstall registry, both hives and both views, which is the list Windows
  shows under Apps, minus updates and system components. Then winget, Chocolatey and Scoop are
  asked what they have, where they are installed. winget's list is the registry list with ids
  beside it, so a row whose name winget also shows is matched *sure*; a name that only matches
  once the version and the "(x64)" are stripped, or a Chocolatey or Scoop slug that matches the
  flattened name or its first word, is *likely; check the id*. A game a launcher installed,
  Steam by its registry key, GOG, Ubisoft, EA, Epic and Battle.net by their uninstall commands,
  is the launcher's to put back: *sign in and it comes back*. Everything else is *by hand*, with
  the publisher's link where the registry had one.
- **Folders.** The drives the reinstall wipes, the Windows drive by default, walked for the
  folders that are not a program: Desktop, Documents, Downloads, Pictures, Videos and Music
  where Windows says they are (a Documents moved to another drive is still Documents, and is
  shown as surviving when that drive is not being wiped), OneDrive beside them, every folder made at the root because
  it was there, the rest of the profile's own folders, one row per program under `AppData\Roaming`, `Local` and `LocalLow`,
  `ProgramData` minus Windows' own, `Saved Games` and `Documents\My Games` named as saves, and
  the browsers named as what they are. Each is measured the way the copy will see it: size,
  files, newest write, with junctions and OneDrive cloud-only placeholders skipped and counted,
  unless *Pull cloud-only files down too* is on, in which case each placeholder is fetched by
  OneDrive as the copy opens it and the sizes say so beforehand.
  `Program Files`, `Program Files (x86)` and `Windows` are not on the list and cannot be added:
  an installed program copied across is a folder that does not run.
- **Extras.** The things that go with the drive and are not folders: fonts installed for this
  user, the `hosts` file, `.gitconfig`, the PowerShell profiles, the PATH, the user's
  environment variables, and the Wi-Fi profiles.
- **Keys.** The Windows key decoded from `DigitalProductId`, the OEM key in the firmware where
  there is one, what the licence service says it was activated with and whether it is licensed,
  and what Office keeps, which is the last five characters. A machine on a digital licence shows
  the generic key Windows writes for itself, and the row says so: the licence comes back on its
  own when the hardware matches.
- **Drives.** Every fixed and removable drive with its free space, and a tick for each the
  reinstall will format.

**Shows.** One step per list on the left, with counts, and a search box in the header that
narrows the programs, the folders and the way back to what matches without touching a tick.
Programs with how each comes back and a *get again* tick for the short list; the person's own
folders as a group of cards at the top of the Folders step with *All* and *None*, then the rest
with a tick each, *Suggested* (the profile, Roaming, LocalLow, saves, browsers and the
root folders; not Local, ProgramData or the caches), *All* and *None*; extras with a tick each;
keys with *Copy* and the honest word for each row, *key*, *digital licence*, *last five only*,
*account*, *not found*. On the right the drives, the destination, and the pack summary: *12
folders, 38 GB, and 5 extras*. A destination on a drive ticked as wiped is refused there, not at
the end; a destination with less room than the ticked folders need says so.

**Writes.** *Pack it up* makes `Rehome 2026-09-21` under the destination and writes, in this
order: `programs.md` to read, `programs.json` for the plugin, `winget-import.json` from winget's
own export, `chocolatey-packages.config` and `scoop-apps.txt` where those matched, `install.ps1`
with one block per manager and the by-hand rows and the launcher games as comments, `keys.txt`,
`to-get.md` and `to-get.ps1` when anything is ticked *get again*, and `rehome-manifest.json`.
Then each ticked folder is copied to
`<root>\C\Users\Dennis\AppData\Roaming\obs-studio`, the old path kept as the new one so the path
itself says where it came from; every file is hashed as it is read and the copy is hashed after
it is written, and *verified* means the two agreed. The manifest is rewritten after every folder,
so a pack stopped halfway still says what arrived. Then the extras go under `extras\`: the Wi-Fi
profiles through `netsh wlan export profile key=clear`, with the passwords in clear, which is the
point of it and why the folder is named for what it is. Nothing is removed from the source: the
wipe is the delete.

**The to-get list.** The full inventory is what was here; the to-get list is what is wanted
back, ticked by hand, one *get again* per row, kept across rescans. *Write the to-get list*
under *Pack it up* writes `to-get.md` to read and `to-get.ps1` to run: the manager's install
line where one knows the program, a *sign in* comment for a launcher's game, and for a by-hand
program the publisher's link with a `winget search --name` line as a hint, since winget may know
it after all under a name the registry did not use. It goes into the folder packed this session,
else straight into the destination.

**The way back.** On the new Windows, *Open a Rehome folder…* reads the manifest: every folder
carried, where it came from, and whether something is already at that path, with its newest
write beside the carried one. Folders with nothing at the destination are ticked; the others
wait for a choice: *keep mine* only adds what is missing, *keep theirs* overwrites, *keep both*
renames what is there to `.old` first. *Bring back what is ticked* copies each to the path it
came from, verified the same way, and says per row how it went. The Wi-Fi profiles go back
through `netsh wlan add profile`, one call per network with what each said; `.gitconfig` and the
PowerShell profiles are copied back; the fonts are handed to the Fonts folder so Windows
registers them; the `hosts` file, the PATH and the variables are shown, not written, because
they need an elevated prompt or a judgement. The Rehome copy stays: it is the only one that
survived the wipe.

## What it refuses to do

It does not copy programs, and it does not delete anything, on the way out or on the way back.
It does not keep the keys: `keys.txt` is written to the drive the user chose and nowhere else,
which is the line between an export and a vault. It does not pretend a package manager knows a
program it does not: winget's own list is the authority, and a *likely* is a match to check, not
one to trust. It does not put the Windows key in for you; `slmgr /ipk` wants an elevated prompt
and Settings > Activation is one click, and the row is there to copy from.

## Watch out for

- A program winget did not correlate, Discord or Brave installed by their own installers, is
  *by hand* even though winget could install it. Asking winget about each by-hand row is a
  network search per program and is not done on Scan.
- Copying `AppData` while its program runs copies a half-written state. Close what matters
  before *Pack it up*; files that cannot be read are listed in the manifest as failed, not
  skipped silently.
- Chrome, Edge, Brave and Opera encrypt saved passwords and cookies against the Windows account
  that is about to stop existing. Bookmarks and history come back; passwords do not. Sign in to
  sync before the wipe. Firefox and Thunderbird carry their logins in the profile and survive
  the copy.
- The way back for a config from an old version under a fresh install of a newer one can be
  worse than none. The row shows both dates; look before choosing.
