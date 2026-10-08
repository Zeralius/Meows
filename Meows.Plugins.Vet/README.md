# Vet

Family PC health: disk room, reboot needed, backup age, diagnostic zip.

Plugin id `meows.vet`.

## What it does

Reads the family PC in big text: every fixed drive with what is free of what there is (red
under a tenth free, or under ten gigabytes whatever the size), whether Windows is waiting
for a reboot to finish updates, and how old the newest file in the backup folder is. A
backup older than seven days — or whatever number the tab is told — counts as stale, and
anything that wants doing stays said on the notification surface until the next checkup says
otherwise. "Export diagnostics" writes one zip with what the checkup saw, for whoever helps.

`Meows.exe --do vet.check` runs the checkup with no window. Every checkup is a line in the
History tab.

## What it refuses to do

No antivirus verdicts, no update installing, no fixing: Vet reads and says, and the zip is
the handoff to a person. The backup folder is only ever read, never written to.
