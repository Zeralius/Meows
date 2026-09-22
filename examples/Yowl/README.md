# Yowl

Yowls when the Windows drive is running out of room. A schedule looks every ten minutes; under
the line a warning goes up with two buttons, *Look again* and *Not today*, and stays until there
is room again or somebody asks for quiet. The line is a setting, kept in the plugin's own
settings file. The Home tab carries the last reading, red while it is under the line.

What it shows:

- `Background.Schedule` for a pass every so often, and `Background.Run` for the same pass on
  demand. The shell owns both and cancels them when the plugin is switched off.
- The check runs on a thread pool thread and marshals back with `Dispatcher.UIThread` before
  touching anything bound.
- `Notifications.SetCondition` with one key and two buttons, and `ClearCondition` under the
  same key the moment the condition no longer applies. A condition is a state, not an event.
- `LoadSettings` with `?? new()` and `SaveSettings` on every change.
- `Log(LogLevel.Warning, …)` for the line that should read as trouble on the Log tab.
- `IGlanceable`: the plugin's own line on its Home card.
- `LanguageWatch`, so the status line follows a language change while the tab is open.

Guide: [section 6, notifications](../../PLUGIN-GUIDE.md#6-notifications) and
[section 7, background work](../../PLUGIN-GUIDE.md#7-background-work).
