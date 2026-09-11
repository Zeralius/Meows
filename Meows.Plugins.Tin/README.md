# Tin

*Where the money is kept.*

Reads the exports and statements your bank already hands out, as CSV or as PDF, and answers the
three things a statement never does: what is recurring, what quietly went up, and what is still
going out every month for something that stopped being used.

Plugin id `meows.tin`.

## No account, no API, no bank login

Point it at a folder of exports and that is the whole setup. Nothing here talks to a bank, asks
for a password, or screen scrapes anything. The export is a file on disk, which is the only shape
this project has ever trusted with anything that matters.

**It is read only, and that is a promise rather than a habit.** These are the most private files
Meows will ever be pointed at, so nothing in the folder is written to, moved, or copied anywhere.
What Tin remembers is a column mapping and a list of things you told it to put away, and both live
with Meows' own settings in `%APPDATA%\Meows`, never beside your statements. There is a test that
compares the folder's timestamps before and after a read.

## More than one account

A folder very often holds statements for two accounts, because they were downloaded into the same
place. Tin sorts them out by itself: **every export names the account it is about** — a Sparkasse
CSV in an `Auftragskonto` column, a PDF in its heading — so the files are grouped by IBAN without
being told anything.

Finding it is safe rather than lucky. A statement is full of long digit strings — mandate
references, customer numbers, contract ids — and two things keep those from reading as accounts:
the **mod 97 checksum** every IBAN carries, and the fact that a country's accounts are all one
length, so in Germany there is exactly one candidate to test rather than twenty.

**Accounts** appears at the top of the left panel when there is more than one, with what each costs
a month. Picking one narrows the whole tab to it: the series, the total, and the files listed.
**Call it** in the header gives an account a name — *Mama*, *savings* — and the masked IBAN stays
underneath, so a name never hides which account it is.

For an export that never names its account, select it under **Exports read** and use **This file
belongs to**. That assignment is remembered by file name; leave it on *whatever the file says* and
detection does the work.

Keeping them apart is not only tidiness. The same standing order leaving two accounts on the same
day is **two** payments, so the duplicate check never reaches across accounts, and a monthly total
that added two people's accounts together would not be a number anybody wanted.

## Getting the exports in

Download them the way you already do, into one folder. CSV or TXT, as many as you like, going back
as far as you like. More history is strictly better: three charges at a steady interval are what
makes something recurring, so a folder holding one month finds nothing at all.

Overlapping exports are expected. Asking for the last ninety days twice in a month means most of
one file is already in the other, so a charge appearing in two files is counted once and the
header line says how many duplicate rows were skipped.

## Statements that only exist as PDFs

Plenty of banks hand out a PDF and nothing else, so Tin reads those too. Drop them in the folder
beside the CSVs, or instead of them.

A PDF has no columns. It has glyphs at coordinates, and everything a person reads as a table is an
arrangement the eye does for itself, so reading one is guesswork with rules.

**There are two shapes, and they have nothing in common.** A printed Kontoauszug is a table: one
row per payment, beginning with a date. An online banking **Umsätze** print is not a table at all —
each payment is three stacked lines, the payee, then the amount alone out at the right margin, then
the details, and no row begins with a date. Both are read, and whichever understood more of the
file wins, because counting what each actually produced is a better judge than any rule about how
the pages look.

| | |
|---|---|
| **Rows** | In a table, a line starting with a date; lines under it with neither a date nor an amount are the wrapped rest of the reference and join the row above. In the stacked shape, the anchor is the amount printed in the money column, the payee is the line above it, and the details are the lines below |
| **The money column** | Found by the right edge the amounts share, because money is right aligned: 9,00 and 1.755,99 begin in different places and end in the same one. A number anywhere else is a contract number, a reference, or the balance in the account heading |
| **The date** | What the payment says about itself first — a card payment stamps the moment into its own details, a direct debit prints the period it covers — and only then the day heading above it. A real statement turned up carrying five headings in forty pages, and heading-first silently dated half a year of payments to one day |
| **Direction** | A minus sign, an `S` or `H` beside the amount, or separate debit and credit columns. Where a file plainly writes its outgoings with a minus, an amount without one is money coming in — that is reading the file's own habit rather than guessing. Failing all of it, **never a guess**: rows that say nothing are refused, because guessing "out" turns a salary into the largest subscription on the tab |
| **The balance column** | Told apart from the amount by how often the two appear together: a debit and a credit never share a row, an amount and a running balance nearly always do. Getting this wrong reads the balance as the payment, which is the one mistake here that produces entirely plausible numbers |
| **The missing year** | German statements print `01.09.` all year long. The year comes from the newest full date on the statement, or the year in its heading, and a date landing more than six weeks after that belongs to the year before, so December's bookings on a January statement do not jump eleven months into the future |

**A scan is not a PDF statement.** If the file is a photograph or a scan of paper, there is no
text in it to read and Tin says so rather than showing an empty tab. Reading that would mean OCR,
which is a different plugin.

CSV is still the surer route where a bank offers both: it says what its columns mean instead of
leaving them to be inferred. XLSX, OFX, QIF and the rest are named and refused rather than passed
over. Anything that is plainly not a statement is left out of the list entirely, so a folder with
a stray screenshot in it does not grow a warning about the screenshot.

## When the columns are not recognised

Every bank writes a different CSV, which is the one thing this plugin can be sure of. The
headings are matched against the words German and English exports actually use, and the preamble
lines banks put above the real header are skipped, so most files are understood without being
told anything.

When one is not, it appears in **Exports read** on the left with a warning and says so. Click it,
and three dropdowns appear at the top of the list: which column is the date, who the money went
to, and the amount. **The correction is remembered against the header line rather than the file
name**, so next month's export from the same bank is understood without being told again.

Amounts are read by counting digits rather than by guessing a country, so `1.234,56`, `1,234.56`,
`12,34`, `€ 4,90`, `8,99-` and `(8,99)` all read correctly out of the same folder. Dates try
European order first, because that is what these exports are.

## What the payment said about itself

Under the payee, each row carries the reference the bank printed with it: the customer number, the
contract, the period it covers. It comes from the **Verwendungszweck** column of a CSV, or from the
detail lines under the amount in a PDF.

It is never grouped on, because it is the part that changes every month. It is there because it is
the part that tells one payee's bills apart: `SWK ENERGIE / VK 21428908 … Strom` and
`SWK ENERGIE / VK 21428897 … Gas` are the same name, the same day and two different contracts, and
the name alone cannot say which is which. The newest one is shown, since that is what the bank is
showing you right now. The full text is on the tooltip when it is too long for the row.

## What counts as recurring

Three charges to the same payee, at intervals that agree with each other. Weekly, fortnightly,
monthly, quarterly, half yearly and yearly are recognised. Two charges is a coincidence, and three
visits to the same shop at random intervals are not a subscription.

The payee is stripped of everything that changes month to month — reference numbers, dates,
mandate ids — and of the words that describe how the money moved rather than who took it. Without
that last part every direct debit in the account groups together under `LASTSCHRIFT` and the
answer is one enormous subscription.

One late month does not break a series: two thirds of the gaps have to agree, so a charge landing
over a weekend is still the same subscription.

**One payee is sometimes several things.** An insurer taking four policies from the same account
on the same day every month is not a rhythm at all when read as one payee: four payments land
together, then nothing for a month, and the gaps read zero, zero, zero, thirty. So each steady
amount is a run, and a run that begins where another ended, one interval later, is chained onto it
as the same thing at a new price. What is left is one chain per thing being billed, each with its
own price rises inside it.

In the list, a company billing several things gets **a row of its own carrying the sum**, with the
parts indented underneath — `SWK ENERGIE 434,00` and under it `233,00` and `201,00`, each with its
own reference saying which contract it is. Selecting the company shows every charge under it, and
putting it away puts away all of its parts. A part that stopped is still listed under its company
but no longer counted in the sum, because a cancelled thing is worth seeing and not worth paying for.

Three more things came off real statements and are handled: an instalment that flickers by a
euro from month to month is one thing rather than two overlapping ones; a price the payee charged
exactly once — a correction, a partial month — is not "the price it used to be" when a rise is
measured; and a charge an order of magnitude off what a payee usually takes, a card payment at
the counter of the city that takes your quarterly property tax, is a stray that does not break
the rhythm.

**Only money going out.** Wages repeat as reliably as anything and are not what this tab is asking
about.

## What it points at

| | |
|---|---|
| **Went up** | The last amount is higher than the one before it. A cent of movement is not a price rise: it has to be at least one percent and at least half a unit of currency |
| **Stopped coming** | The next one was due and never arrived, with a grace period of about a third of the interval. Either it was cancelled, or it failed and nobody noticed |
| **Put away** | Things you have said are not subscriptions |

Everything is also given as a monthly figure, whatever its actual rhythm, so the top of the tab
can add it up into what all of it costs a month and a year. That total is the number the tab
exists for.

## Where it goes, and what a month usually does

**Where it goes** in the header opens a panel on the right. The ring is every payment going out,
grouped by company, biggest first, with everything past the eighth gathered into one wedge. It
groups more coarsely than the list does, on purpose: an insurer taking three policies is one wedge
here and three rows in the list, because a chart answers where the money went and the list answers
what it was for.

Under it, what a month usually does — for **next month** and for **a year at this rate** — in two
halves that are kept apart because they behave differently:

| | |
|---|---|
| **On a known rhythm** | The subscriptions, counted at their own cadence. A yearly premium counts once a year rather than a twelfth of it every month |
| **In a typical month besides** | Everything that does not repeat — the shopping, the odd repair — taken as the **middle** month rather than the average, so one catastrophic month does not raise the estimate of every month after it |

Money coming in is worked out the same way, because a wage or a pension arriving every month is
exactly as much a rhythm as a subscription leaving.

**Only whole months count.** The first and last months of a folder of exports are nearly always
partial, and a half month read as a whole one drags the answer down. Under two whole months it
says so rather than guessing.

It is what the last few months did, not a promise, and the panel says as much underneath.

## Putting things away

Rent, tax, and a standing transfer into savings all repeat exactly like a subscription does, and
nothing in the file says which is which. **Not a subscription** hides one, and they stay reachable
under **Put away** with a button to bring them back. Hidden entries are left out of the monthly
total, because they were hidden for being something other than a subscription.

## What it does not do

- **It does not tell you what to cancel.** It shows what is going out and how often, and stops
  there.
- **It is not advice.** There is no budgeting, no categorising into pie charts, and no opinion
  about whether any of it is a good idea.
- **It does not notify.** Nothing here is true whether or not the tab is open in the way a missing
  tool or a passed deadline is, so it stays in the tab where the numbers are.
- **One amount column only, in a CSV.** Exports that split debit and credit into two columns are
  not handled yet; point the amount at whichever column carries the money going out. PDFs do
  handle the split, because there the columns are all there is to go on.
- **It does not read scans.** A PDF holding a picture of a statement rather than text is refused
  with the reason. OCR belongs somewhere else.

## Currency

There is one setting for it, in the header, because a CSV rarely says which currency it is in and
guessing one is worse than being told. It is only ever used to write the number down. Amounts are
formatted the way the language the window is in writes them, not the way the machine does.
