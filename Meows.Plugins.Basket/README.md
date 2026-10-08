# Basket

Local task board with lists, cards and due dates. No account, no cloud.

Plugin id `meows.basket`.

## What it does

Reads one settings file with the board: lists in order, cards in order, each with a title,
an optional due day, notes, a checklist and an optional link to a file or folder. Shows the
lists on the left, the selected list's cards in the middle, and the selected card on the
right. Cards move between lists with the arrow buttons or the move-to dropdown.

What falls due is said out loud: a condition on the notification surface while anything is
overdue or due within seven days, one Home line with the same sentence, and a `due` job for
`Meows.exe --do basket.due` so a scheduled task can ask with no window. Every add, move and
finish is a line in the History tab, and a rule can add a card (`basket.add`) when another
plugin records something. Dropping files on the tab, or handing them over from another tab,
makes one linked card per file. Export writes the board as markdown for someone without Meows.

## What it refuses to do

No sharing, no sync, no comments, no permissions. One board, one machine. If a card has to
leave the machine, export it and send the file.
