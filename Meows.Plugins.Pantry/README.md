# Pantry

Recipe box with expiry dates and a weekly cooking plan.

Plugin id `meows.pantry`.

## What it does

Keeps three small lists: recipes (a title, markdown body, minutes and tags, typed by hand),
what is in the fridge with the date it runs out, and the week's cooking plan, seven days from
today with one recipe per day. Past days fall off by themselves; the plan always starts today.

What ran out, or runs out within three days, is said out loud on the notification surface
and on the Home line. "Suggest tonight" draws one recipe from the box into today's plan, and
the Cook button journals what was made. A markdown or text file dropped on the tab becomes a
recipe, title from the file name. Every add, suggestion and cooked meal is a line in the
History tab.

A rule can fill an empty tonight (`pantry.suggest`), and `Meows.exe --do pantry.expiring`
reports the fridge with no window.

## What it refuses to do

No ingredient parsing, no nutrition maths, no store integration, and no photos: the recipe
is text you typed or a file you dropped, and the plan is seven days, never more.
