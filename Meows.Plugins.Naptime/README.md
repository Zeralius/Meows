# Naptime

Habit tracker: daily ticks, weekly targets, streaks with one miss forgiven.

Plugin id `meows.naptime`.

## What it does

Keeps small habits: a name, how many times a week it is wanted (one to seven, Monday to
Sunday), and the days that got their tick. Tick the box and the day is in; untick it and
today's line in the history goes with it, not the habit.

Targets read "2 of 3 this week", and streaks count days back with one missed day forgiven —
two in a row break the run. The header says today's ticks and weeks met, and two days with
nothing ticked anywhere stays said on the notification surface until something gets its
tick. Every tick is a line in the History tab.

A rule can tick the habit it names (`naptime.tick`): asking twice for the same day is one
tick, not two, and a name with no habit is declined rather than invented.

## What it refuses to do

No reminders at set times (the quiet-days condition is the nudge), no graphs, no sync: the
ticks live in one file, and the week starts Monday.
