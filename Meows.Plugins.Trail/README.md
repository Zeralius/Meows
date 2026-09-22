# Trail

*Follows the trail.*

PATH, read and explained. Every entry in the order Windows actually searches it, what is wrong
with each one, and — the question that costs the afternoon — which copy of a typed command wins.

On the machine this was written on: 35 entries, six of them the same folder listed twice, two
stray semicolons, and one folder that has not existed for a while. Answering that today means a
terminal, `where`, and counting semicolons in a dialog box that has not meaningfully changed
since Windows 95.

## What it says about an entry

Each row shows the entry as it is stored, what it expands to when that differs, which scope it
came from and where it sits in the search order. A fault is a fact about the string or about the
disk, never an opinion about whether the entry ought to be there:

| | |
|---|---|
| **not there** | the folder does not exist |
| **duplicate** | the same folder is earlier on the list, so this one is never looked at |
| **empty** | a semicolon with nothing after it, which older Windows read as the current folder |
| **malformed** | quotes or characters no path can hold, so it will not resolve as written |
| **unexpanded** | a variable in it that nothing defines, so it resolves to nonsense |

Two entries are the same folder when they are the same folder: a trailing slash and a different
capitalisation are exactly how one folder gets onto the list twice without looking like it has.

## Which one wins

Type a command and Trail says where it would come from, at what position, and lists every
further copy that will therefore never run. Folder order comes first and `PATHEXT` second, which
is the part people get backwards: a `tool.cmd` in an earlier folder beats a `tool.exe` in a later
one, even though `.EXE` comes before `.CMD` in `PATHEXT`.

The path it prints is the name as it is spelled on disk, not as `PATHEXT` spells it. `PATHEXT` is
upper case, and `dotnet.EXE` is a path somebody will paste somewhere it does not work.

## The one verb, and its fences

**Take the ticked ones off** removes faulty entries from PATH. A bad PATH does not break a
terminal, it breaks the logon, so:

- **Your half only.** The machine's entries are shown, and cannot be ticked. Changing them needs
  elevation Meows does not ask for.
- **The old value is written down first**, into the plugin's own data folder, with the time in
  the name. If that write fails, nothing is changed: an edit with no way back is the one thing
  this plugin must never do. **Put the last one back** restores it.
- **The raw form is kept.** `%JAVA_HOME%\bin` goes back as `%JAVA_HOME%\bin`. Reading PATH through
  `Environment.GetEnvironmentVariable` expands variables on the way out, and writing that result
  back would freeze the entry to wherever the variable pointed today; so the registry is read raw
  with `DoNotExpandEnvironmentNames` and written back as `REG_EXPAND_SZ`. `Environment.SetEnvironmentVariable`
  writes a plain string instead, which silently ends the expansion for good.
- **The plan is made against the stored string**, not against what is on screen, so an entry that
  changed since the tab was read cannot be removed by position. Asking to drop one of two
  identical entries drops one of them.
- **The change is announced** with `WM_SETTINGCHANGE`, or the registry holds the new value while
  every open program carries on with the old one until the next logon, which looks exactly like
  the edit not having worked. Programs already running keep the list they started with whatever
  is broadcast, and the tab says so.

## Where the code is

[`Services/PathRead.cs`](Services/PathRead.cs) reads and judges, and does it from three strings
rather than from the machine, so the judging is tested against a PATH written for the purpose
instead of whatever this computer happens to have installed today.
[`Services/PathWrite.cs`](Services/PathWrite.cs) is the half that writes, in a file of its own
because it is the half that can do harm.
