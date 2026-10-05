# Backlog

Which game to play next: backlog, ratings and a picker.

Plugin id `meows.backlog`.

## What it does

Reads Steam's own manifests (the same ones Larder reads) and folds every installed game into
a pile: waiting, playing, shelved or done, with a rating once it can be given one and a note
for anything worth remembering. New arrivals land on the pile by themselves; nothing ever
leaves it by itself, not even a game that is no longer installed. "Pick for me" draws one
weighted game for tonight — unrated counts double, a star counts once, and what is already
being played gets one extra — and says it out loud. A game Steam never heard of can be added
by hand; it has no size and no played time, and keeps them that way.

A rule can ask for a pick (`backlog.pick`), and `Meows.exe --do backlog.pick` says tonight's
game with no window. Every pick, start and finish is a line in the History tab.

## What it refuses to do

No playtime tracking (manifests do not carry hours), no achievements, no store integration,
and nothing is ever uninstalled from here: forgetting what the pile thought never touches a
game folder.
