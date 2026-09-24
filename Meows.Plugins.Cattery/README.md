# Cattery

*Where the strays are kept.*

Every git repository under the folders your projects live in: its branch, what is not committed,
what is not pushed, and how long since anyone committed, with the most neglected first. On the
machine this was written for, 26 of 38 repositories had uncommitted work in them, and the posting
bot carried 164 uncommitted lines for long enough that its history and its working copy drifted
apart.

Plugin id `meows.cattery`.

## Where it looks

Add the folders on the right. Each is walked a few levels down for a folder holding `.git` (a
folder, or a file for a worktree). The walk stops at a repository instead of going into it, and
never enters `node_modules`, `bin`, `obj`, `.vs`, `target`, `dist`, `build`, a virtual
environment or a hidden folder, so a drive of projects is a quick walk and a vendored repository
inside another is not counted twice.

## What it asks

Git itself, twice per repository: `git status --porcelain=v2 --branch` for the branch, the
changed, new and conflicted files, and ahead and behind the upstream; and `git log -1` for the
last commit's date. Both only read, with `--no-optional-locks` so a status never writes the index
under another program's feet. Nothing fetches, because that needs the network and your
credentials, so ahead and behind are as of the last fetch. Four repositories are asked at a time,
in the background, and the last answer is kept, so the tab opens on it while git is asked again.

A repository git will not read (the "dubious ownership" of a folder another account made, a
corrupted index) shows git's own words rather than a guess.

## How it is ordered

*Most neglected first*: uncommitted work, oldest last commit first; then commits that are not
pushed, or a branch with no upstream at all; then everything else. A clean, pushed repository is
not neglected however old it is; it is finished. *By name* and *latest commit first* are the other
orders, narrowed to uncommitted work or not pushed if wanted. Home's card says how many have work
sitting in them and which has sat longest. **Open folder** and **Terminal here** (Windows Terminal,
or PowerShell when that is not installed) are the way in.

## What it refuses to do

Anything that writes. No commit, stash, pull, push, fetch or checkout: it is a list of what needs
doing, and the doing belongs to the terminal it opens.
