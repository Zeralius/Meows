# Purr

Says what Meows is watching, when it last looked, and which watch quietly stopped.

Plugin id `meows.purr`. Plain name **Watches**.

## What it shows

Since 2.0 Meows keeps running in the tray, and several plugins keep looking at things while the
window is closed: Collar at its dates, Birdwatch at its accounts, Saucer at the clipboard. The
tray dot says *something happened*. Nothing said *what is still happening*, and in particular
nothing said that a watch had stopped.

One line per watch: which plugin, what it called it, how often it looks, when it last looked,
when it will look next, and how many times it has looked so far. Selecting one shows the last
thing the pass reported. The header counts watches and plugins, and how many have stopped.

**A stopped watch is the point.** When a pass throws, the shell posts an error notification and
the schedule ends; that is the design, since a watch that failed once is likely to fail again a
minute later, forever. But a notification is read once and dismissed, and the tab it came from
looks the same afterwards. Purr keeps the stopped watch at the top of the list, with the reason,
until its plugin is switched off and on again.

## What it reads

The shell's own list, through `IMeowsHost.Watches` (contract 0.7.0). Every `Schedule` any plugin
registers is on it, with the times the shell records as passes finish. Purr owns none of it and
changes none of it. It is on the list itself: a half-minute tick that re-reads the words, since
"3 min ago" is only true for a minute.

## What it is not

Not the Tasks panel. That shows work in progress, including one-off scans, and goes quiet when
nothing is running. Purr shows what is scheduled whether or not it is running this second, and
what has stopped, which the Tasks panel by design forgets.

Not a place to change intervals. Each plugin chose its own; Purr says what was chosen.
