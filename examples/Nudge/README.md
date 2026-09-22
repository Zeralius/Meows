# Nudge

Both halves of a handoff. Pick a folder and nudge Chonk or Purrge with it; the receiver's
answer, "3 sets, 12 copies", lands back on the tab when it is done. Anything another plugin
sends this way, Chonk's *Send to…* or Kibble's pick, is listed as it arrives, and the sender is
answered.

What it shows:

- `Handoff.Folder` with a `Reply`, `Handoff.CanReach` to enable the button only when the
  receiver is installed, and `Handoff.Send` returning false when it is not there, would not
  open, or does not take folders.
- `IHandoffTarget` on the view model: `Accepts` for the shell to ask first, `Receive` on the UI
  thread with the tab already in front, and `Answer` for the sender that wants to know.
- `KnownPlugins` for the ids, so nobody spells one wrong.
- `Pick.Folder` from a view model, with no `TopLevel` in hand.

Guide: [`Handoff`](../../PLUGIN-GUIDE.md#handoff) and [`Pick`](../../PLUGIN-GUIDE.md#pick) in
section 4.
