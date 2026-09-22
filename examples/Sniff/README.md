# Sniff

Sniffs a folder: how many files, how many bytes, the biggest one. Long enough on a real folder
to want the Tasks panel, a progress bar and a Cancel, which is what it is for. The tally can be
written out through the shell's save dialog.

What it shows:

- `Background.Run` for one job, held in a field so a second press cancels the first, and
  `IBackgroundTask.Cancel` behind a button.
- `IBackgroundContext`: `Report` for a status a person would read, `ReportProgress` from a top
  level listing so the bar has something to count against, and `Token` in every loop.
- `OperationCanceledException` let through, not swallowed, so the shell sees a clean stop.
- `Dispatcher.UIThread.InvokeAsync` around everything bound; the walk itself runs on a thread
  pool thread.
- `Pick.Folder` and `Pick.Save` with a title, a suggested name and a filter.
- `RelayCommand` with `canExecute` and `RaiseCanExecuteChanged` when the inputs change.

Guide: [section 7, background work](../../PLUGIN-GUIDE.md#7-background-work),
[section 8, MVVM and threading](../../PLUGIN-GUIDE.md#8-mvvm-and-threading) and
[`Pick`](../../PLUGIN-GUIDE.md#pick) in section 4.
