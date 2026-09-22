# Yarn

A list of things to remember, and a record of when each was kept. The list is settings; each
keep goes to the shared store as an event, so it shows on the History tab and on the plugin's
card, and *Put back* on that line takes the thread off the list again for as long as it is
still there. Ctrl+K finds a thread whether the plugin is on or off; found while off, the hit
switches Yarn on and selects it.

What it shows:

- `Store.Record` with a kind to filter on, and `Store.Recent` to read the journal back.
- `ISearchable` on the view model, answering from memory with `SearchWords.Match`.
- `IMeowsPlugin.WhileOff` returning an `ISearchable` built from settings through
  `IMeowsDormantHost`, and a handoff to the plugin's own id as the way a hit lands.
- `IHandoffTarget` taking that one private verb.
- `IUndoTarget`: `CanUndo` by looking, `Undo` by doing, a sentence when it cannot.
- `IGlanceable`: how many threads and the newest, on the Home card.

Guide: [being searched while switched off](../../PLUGIN-GUIDE.md#being-searched-while-switched-off)
in section 3, and [`Store`](../../PLUGIN-GUIDE.md#store) and
[being searched from Ctrl+K](../../PLUGIN-GUIDE.md#being-searched-from-ctrlk) in section 4.
