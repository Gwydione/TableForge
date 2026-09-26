# TableForge

**TableForge turns random tables from RPG books and PDFs into fast, searchable, rollable digital tables.**

Version 1.0.0-rc18 (release candidate) · Windows 10/11 (64-bit) · by RPG Frequencies

Paste a table copied from a PDF or book, let TableForge interpret it, correct anything it got wrong, save it, and roll it
whenever you need it at the table:

**Paste → Review/Correct → Save → Find → Roll → Read**

TableForge can also:

- clean up difficult PDF-copied table text (wrapped lines, broken hyphenation, stray characters, several columns);
- organize tables into **Collections** and **Folders**, with search and recent tables;
- roll with fast **Built-in dice**, or optionally with **dddice** 3D visual dice, including themes from your own
  dddice account;
- follow **linked tables** ("Scavenging → Scavenged Items") and roll **inline dice** inside results ("gain 1d4 Armor");
- make **several rolls at once**, keeping every result;
- **Copy Table Text** as clean plain text for other tools and VTTs.

## Install

1. Download `TableForge-<version>-Setup.exe` from **[GitHub Releases](https://github.com/Gwydione/TableForge/releases)**.
2. Run the installer. It installs for your Windows user only and needs no administrator rights.
3. Start **TableForge** from the Start Menu.

- **Windows x64 only** for now (64-bit Windows 10 or 11).
- **.NET is included**; nothing else needs installing.
- The **Microsoft Edge WebView2 Runtime** is needed only for the optional dddice visual dice. It comes with Windows 11 and
  current Windows 10. Built-in dice always work, with or without WebView2 and dddice.
- To upgrade, run a newer installer: it replaces the program and keeps your tables. A portable
  `TableForge-<version>-win-x64.zip` is also published for people who prefer not to install; it keeps its data in the same
  place.

**SmartScreen:** early TableForge release candidates are not code-signed, so Windows may show "Windows protected your PC"
when you run the installer. If you downloaded it from the [TableForge Releases page](https://github.com/Gwydione/TableForge/releases) and trust it, choose
**More info**, then **Run anyway**. You can compare the download with the `SHA256SUMS.txt` published beside it
(`Get-FileHash TableForge-<version>-Setup.exe` in PowerShell). Don't turn off SmartScreen or other Windows protection.

## First use

1. Type a name for a **Collection** (a game or campaign) at the top left and press **Enter**.
2. Choose **Paste Table…** and paste a random table copied from a PDF or book.
3. Choose **Interpret**, then review and correct the rows (notes point out anything TableForge was unsure about).
4. **Save Table**.
5. **Roll**.

## Your data and backups

Everything TableForge keeps (your tables, settings, dice choice and any dddice connection) is in one folder:

```
%LOCALAPPDATA%\TableForge
```

- **Open Data Folder**, at the bottom of the TableForge window, opens it.
- **Uninstalling TableForge does not delete this folder.** Reinstalling finds your tables again automatically.
- **To back up**, close TableForge and copy the whole folder somewhere safe. To restore, put it back.
- When a future version needs to update your data's format, TableForge first saves an automatic safety copy of your
  database beside it (and changes nothing if it cannot).

## Privacy

TableForge keeps its data on your computer and has **no telemetry or analytics**. The optional dddice dice make network
requests to dddice; TableForge never asks for, collects or stores your dddice password, and a connected dddice account's
access token is protected with Windows DPAPI for your Windows user. See [PRIVACY.txt](PRIVACY.txt) for the details.

## Known limitations

- Text is taken from what you copy: image-only or scanned PDFs need OCR first, which TableForge does not do.
- Unusual PDF layouts may need some manual cleanup on the Review screen.
- TableForge does not find tables in a whole PDF by itself; you copy the table you want.
- **Copy Table Text** produces generic tab-separated text, not a format for any particular VTT.
- dddice dice need an internet connection and the WebView2 Runtime.
- Connecting a dddice account takes a few steps in your web browser.
- Windows x64 only for now.
- Only one copy of TableForge should be open at a time, and closing the window discards unsaved Review edits without asking.
- The parser is deliberately conservative: anything it is unsure about stays together and is flagged on the Review screen
  instead of being guessed. Side-by-side columns that each restart at the same number are not split.
- dddice accounts can use only themes with every standard die; custom dice are not supported.

## Feedback and bug reports

Please report problems and ideas on **[GitHub Issues](https://github.com/Gwydione/TableForge/issues)**. It helps to include:

- your TableForge version (bottom right of the window, or **About**);
- your Windows version;
- what you were trying to do, and what happened instead;
- for table-reading problems, the table text you pasted (or a screenshot of it);
- the exact error message, if there was one.

**Never post dddice tokens, `dddice-account.json`, or any account credentials.**

## License

TableForge is copyright (c) 2026 RPG Frequencies, all rights reserved; see [LICENSE.txt](LICENSE.txt). Third-party
components are listed in [THIRD-PARTY-NOTICES.txt](THIRD-PARTY-NOTICES.txt).

---

# Using TableForge in detail

## Everyday use

1. Type a name in the box at the top left (for example a game or campaign) and press **Enter** to create a collection.
2. Choose **Paste Table…**, paste the copied text, and choose **Interpret** (or **Ctrl+Enter**). Check the result on the
   Review screen — notes beside rows say where the parser was unsure — then **Save Table**.
3. Choose a table to open it, then **Roll** (or type a number and press **Enter** to resolve a specific roll).
4. If a result links to another table, a button appears; following it never rolls automatically.

Keyboard: in the table search box **Enter** opens the first match and **Escape** clears the search. In the table list the
arrow keys move the highlight and **Enter** opens. In “Paste rows into this set…”, **Escape** closes it and **Ctrl+Enter**
adds the rows. On the Review screen, **Ctrl+J** joins the selected row into the row above it (see below).

**Recent** (under the search box) lists the tables you opened most recently in the current collection. **Recent rolls**
(bottom left) shows the last 10 rolls anywhere as read-only snapshots; clicking one reopens its table (a snapshot of a
deleted table stays readable but cannot be opened).

## Data folder details

One SQLite database file:

```
%LOCALAPPDATA%\TableForge\tableforge.db
```

(typically `C:\Users\<you>\AppData\Local\TableForge\tableforge.db`). It holds your collections, tables, recent tables and
recent rolls. The location is fixed in V1. **Open Data Folder**, at the bottom of the main window, opens this folder. To back
up your data, close TableForge and copy the whole `TableForge` folder somewhere safe; to start fresh, delete it.

Beside it TableForge keeps `dice-provider.txt` (one word, `builtin` or `dddice`: your dice choice) and, only if you ever choose
dddice, a `WebView2` folder (the browser profile that draws the dice; it holds no tables). If you connect a dddice account there is
also `dddice-account.json`: the connection's token, encrypted with Windows (DPAPI, readable only by your Windows user), plus your
account's display name, chosen theme and dddice room. Disconnect deletes it. Deleting it by hand just returns dddice to guest.

On first launch TableForge creates the folder and database itself. When a newer TableForge needs to update an existing
database to its format, it first saves a copy beside it, `tableforge.pre-v8-from-v7.backup.db` (the format it updates to,
and the one it came from), and updates nothing if that copy cannot be made. If an update then fails, TableForge says so and
where the copy is (to go back, close TableForge and copy the backup over `tableforge.db`). The newest three copies are kept.
Nothing is copied on an ordinary start. A database from a newer TableForge is never opened or changed: TableForge asks you
to install the newer version.

The legal and privacy notes are `LICENSE.txt`, `THIRD-PARTY-NOTICES.txt` and `PRIVACY.txt` (also installed with the program).
TableForge collects no telemetry.

## Dice expressions

A table's dice are an ordinary `NdM` with **one optional fixed modifier**: `d20`, `2d6`, `2d6+1`, `d20-2`, `3d8+4`
(case does not matter, and the count may be omitted). That is all: no second term, keep/drop, exploding dice, multiplication or
variables. Anything else is refused with a message rather than partly understood.

The legal results include the modifier: `2d6+1` produces 3–13 and `d20-2` produces -1–18, and a table is checked against that
final range. When you type a roll by hand, enter the **final** result (dice already added to the modifier, e.g. 9); TableForge
never adds the modifier again. Rows for negative results (only for dice that can go negative) are written like `-1-0`.

### `2d6` and `d66` are different

- **`2d6`** rolls two six-sided dice and **adds** them: 3 and 5 make **8**. Legal results are every number from 2 to 12.
- **`d66`** rolls two six-sided dice and **reads them in order as tens and ones**: 3 then 5 makes **35**, and 5 then 3 makes 53.
  The legal results are exactly the 36 numbers whose two digits are both 1–6:

  ```
  11 12 13 14 15 16
  21 22 23 24 25 26
  ...
  61 62 63 64 65 66
  ```

  Numbers such as 17, 20, 27, 40 or 60 cannot occur, so a complete d66 table (`11-16`, `21-26`, … `61-66`) has no gaps, and a row
  for an impossible number (or a range such as `15-22` that runs through 17–20) is flagged on the Review screen.

The dice you write decide which one it is; TableForge never guesses from the rows. A heading such as `D66 RANDOM ENCOUNTER` or
`NAME GENERATOR (D66)` is a d66 table, `2D6 RANDOM ENCOUNTER` is a 2d6 table. `d66` takes **no modifier** (`d66+1` is refused).
Only a bare `d66` is this convention: an explicit count (`1d66`, `2d66`) still means a die with 66 sides, as it did in earlier
release candidates. Typing a roll by hand for a d66 table means the final number (`35`), and impossible numbers are refused.
The roll shows as "Rolled 35 (d66)", and with dddice a d66 is simply two ordinary d6 whose order becomes tens and ones.

## Copy Table Text

**Copy Table Text** puts a clean plain-text copy of a table on the clipboard, ready to paste into a VTT, a document or any other
tool. It is on the Roll screen (beside the table's name) and on the Review screen (beside **Save Table**), so a freshly
pasted table can be copied out without saving it (it is offered there whenever **Save Table** is). The text is:

```
RANDOM STARTING GEAR
d10

1-2<TAB>Backpack
3<TAB>Knife
```

one result set at a time (with several, a chooser beside the button says which), the name, the dice, a blank line, then one
row per line with a tab between range and result. Ranges keep how they were written (`00`, `08`, `96-00`, d66 as `11`–`66`).
Result text is only trimmed, with line breaks and tabs turned into single spaces; links, inline-roll results, the modifier,
clamp results and roll history are never included, and copying changes nothing on screen.

## Several rolls at once

Beside **Roll** is **Rolls:** (1 by default). Choose 2 to 10 and the button reads **Roll 3 Times**: one press makes that many
independent rolls of the current table, one after another (with dddice each set of dice settles and its result appears before
the next throw). Every result is kept and shown separately, headed **Roll 1**, **Roll 2**…, each with its own links and inline
dice buttons; nothing is combined, counted or interpreted, and table text such as "roll 3 times" is never read as a count.

- Each roll is a normal roll: the table's own modifier (`2d6+1`) and Clamp apply to every one, and each is its own entry in
  **Recent rolls**.
- The **Modifier** box is used by the first roll that succeeds, then resets to 0 as usual; it is not added to every roll.
- If a roll fails or is cancelled, the ones already made stay (and stay recorded); the rest are not rolled, and TableForge says
  where it stopped.
- A roll you type yourself is always one result. The choice goes back to 1 when you follow a link or open another table, and is
  not remembered after TableForge closes. It is locked while dice are rolling.

## Situational modifier

Beside **Roll** is a small **Modifier** box (`0` by default). Type one whole number from `-1000` to `+1000` (`3`, `+3`,
`-2`; blank means 0) and the next **Roll** adds it to what the dice produced: a d20 that rolls 11 with `+3` is looked up as
**14**, shown as "Rolled 14" with "11 +3 situational" underneath. The box then goes back to 0. It is for one roll at a
time; nothing about it is saved with the table, and the table's own dice (`2d6+1`) are never changed by it.

- The sum is used exactly as calculated, even outside the table: a d6 that rolls 1 with `-1` is **0**, which usually
  shows "No entry covers 0." TableForge never rerolls or picks the nearest row, and clamps only a table you have
  explicitly set to (see **Clamp to table range** below). That roll still uses the modifier up.
- Anything that is not one whole number in range (`+`, `2.5`, `1d4`, `+1001`) turns Roll off and says why; it is never
  read as 0. **Enter** in the box rolls.
- If the dice cannot finish (a dddice roll is cancelled or fails), the modifier stays for the retry. dddice still shows
  the real die (11); TableForge adds the modifier once the dice settle. The box is locked while dice are in the air.
- A roll you type yourself is already final, so it ignores the modifier and leaves it waiting. Inline dice rolls ignore
  it too.
- Opening a different table, or following a link, starts the box at 0 again.
- A d66 table has no Modifier box: arithmetic does not respect its tens-and-ones results (35 + 2 is not a d66 result).
- **Recent rolls** keeps the final number and the modifier used, e.g. `14 (+3)`; hover for "d20 → 14 (11 +3 situational)".

## Clamp to table range

A table can opt in to **Clamp out-of-range rolls to table range** (a checkbox under the name and dice on Review / Edit
table; off for every table unless you turn it on). Then a roll that lands below the table's lowest row uses that lowest
value, and one above its highest row uses the highest value. The range is the one the rows actually cover (a d20 table
whose rows run 5–15 clamps 3 to 5 and 18 to 15), not the dice's theoretical range.

- The calculated number is never hidden: a d6 rolling 6 with `+2` shows "Rolled 8", "6 +2 situational", then
  "Resolved as 6 (clamped)" and row 6's result. Stored modifiers count the same way (`d6+1` rolling 7 on rows 1–6 uses 6).
- Only values outside the outer boundaries move. A gap inside the table (rows 1–3 and 5–6, roll 4) is still "No entry
  covers 4.", and overlapping rows are still reported as ambiguous.
- A roll you type yourself is never clamped, and neither are inline dice. A followed link uses the destination
  table's own setting.
- Every result set must share the same lowest and highest value, so one roll clamps to one number for all of them;
  otherwise the checkbox is unavailable and says why. It is never available for d66. Editing a clamped table into one of
  those shapes turns Clamp off when you save (the checkbox shows that before you do).
- **Recent rolls** keeps both numbers, e.g. `8 (+2) → 6 (clamped)`, and the result of the row actually used.

## Inline dice in results

When a result's own text contains a dice expression TableForge already knows how to roll — `You gain +1d4 Armor`,
`Encounter 2d6 Skeletons`, `Gain 2d6+1 supplies`, `Consult entry d66` — a **Roll** button for that expression appears
beneath it once the result is showing. Pressing it rolls through whichever dice provider is currently selected (Built-in
or dddice, with the same visible dice and the same wait for dddice to settle) and shows the result **resolved in
context** on a smaller line under the original text:

```
You gain +1d4 Armor
Resolved: You gain +3 Armor
[Roll Again]
```

Only the dice expression itself is replaced, by its final number (dice plus the expression's own modifier: `2d6+1` → `9`);
everything around it — a leading `+`, commas, full stops, the words — is kept exactly as written. TableForge does not
fix grammar (`Encounter 1 Skeletons` is shown as is). **Roll Again** replaces the number; it does not keep a history.
The original text is never rewritten, in the table or anywhere else.

A result with more than one supported expression (`Gain 1d6 food and 1d4 water`) offers one button per distinct
expression (`Roll d6`, `Roll d4`, then `Roll d6 Again` / `Roll d4 Again`); an expression not rolled yet stays as written
in the resolved line (`Resolved: Gain 4 food and 1d4 water`). The same expression repeated in one result (`Gain 1d6 gold
and lose 1d6 reputation`) gets only one button, and its result fills every occurrence (`Gain 4 gold and lose 4
reputation`). Text such as `UD6` or `4d6kh3` that only looks like dice notation is left alone: TableForge never guesses
at a second, more permissive dice grammar, only the same one a table's own dice already use.

These rolls are auxiliary: they never appear in Recent Rolls, ignore the Modifier box (and leave it as it is), and never
re-resolve the table or follow a linked result — a result that both contains dice text and links to another table offers
both actions independently. A roll that fails or is cancelled leaves the previous resolved line unchanged. Inline results
are transient: rolling the parent table again, following a link, or opening another table clears them; nothing is saved.

## Dice: Built-in or dddice

The **Dice** choice near the top of the left pane (just under **Paste Table…**) picks how tables are rolled:

- **Built-in** (the default): rolls instantly, works offline, needs nothing.
- **dddice** (3D dice, needs internet): press Roll, watch the dice tumble in a panel above the table, and the table result
  appears when they settle. No dddice account is needed: TableForge uses dddice's anonymous *guest* access (free
  `dddice-bees` dice). Choosing dddice shows "Preparing dddice…" and enables Roll once the dice are ready (a few seconds).
  A guest identity is created once per TableForge session and is never saved.

dddice rolls only the base dice (`2d6+1` shows a 2d6); TableForge adds the modifier itself, so the result is the same kind of
number Built-in produces. It shows d4, d6, d8, d10, d12, d20 and d100 (a tens die plus a ones die), in any count. A table with
other dice (a d3, d7…) says so and can be rolled with Built-in. Typing a roll yourself never contacts dddice.

If dddice cannot start or a roll fails (no internet, dddice down or rate-limiting new guests, script or connection failure, a roll
that does not finish in 30 seconds), TableForge says why and offers **Use Built-in Dice** (and **Try again**). It never rerolls
with Built-in by itself, and Recent Rolls only ever records results the dice really produced. If dddice was your choice last
time and cannot start, TableForge opens normally and offers Built-in. Keep the TableForge window open (not minimised) while
the dice roll.

**What dddice sees:** the kind of dice rolled (for example d20, or d10 with a tens die), the anonymous guest identity, a room
named "TableForge", the rolls made in it, and normal web traffic (its script and dice models from dddice's CDN, and a
WebSocket). TableForge never sends table names, table text, results or collection names. Nothing dddice-related is contacted or
created unless you choose dddice.

**Requirements for dddice:** internet, and the free *Microsoft Edge WebView2 Runtime* (Evergreen). It is preinstalled on Windows 11
and on current Windows 10 with Microsoft Edge; if it is missing TableForge says so and Built-in still works. The publish folder adds only
`Microsoft.Web.WebView2.*.dll`, `WebView2Loader.dll` and `Assets\dddice-host.html` (about 2 MB in all); no runtime is bundled.

### Connecting a dddice account (optional)

Guest dice need no account and stay the default. To roll with a theme from your own **Digital Dice Box**, press **Account…**
under the Dice choice (the sidebar then shows `dddice: <your name>` and `Theme: <theme>` instead of `dddice: Guest`):

1. **Connect** shows a short code. Press **Open dddice** (it opens `dddice.com/activate` in your own browser), sign in there if
   asked, and enter the code. **Copy** copies just the code. TableForge never asks for, sees or stores your dddice password.
2. TableForge notices the approval by itself (it checks every 5 seconds; **Cancel** stops, and the code expires after about
   5 minutes: "Connection timed out. You can try again."). Nothing is saved until you approve.
3. Your Dice Box is listed. A theme can be chosen only if it has every standard die TableForge rolls: d4, d6, d8, d10, the
   percentile d10x, d12 and d20, all with ordinary numbered faces. Other themes stay listed, greyed out with the reason
   ("Missing d12, d20", "No d10x (percentile)", "Non-standard d6 faces"). **Refresh** reloads the list.

The connection and theme are remembered across restarts, and one dddice room is created for the account and reused. The account
is checked with dddice only when dddice is actually used. If dddice refuses it, TableForge says "dddice connection expired.
Reconnect in Account…" and keeps it until you reconnect or disconnect; if dddice is busy or unreachable it says so and keeps
everything. If your chosen theme leaves your Dice Box or can no longer be used, TableForge says so and asks you to choose
another. It never switches to the guest dice or another theme by itself; **Use Built-in Dice** is always there.
**Disconnect** forgets the connection on this computer only (your dddice account is not changed) and dddice rolls as a guest again.

Not included yet: custom dice, OBS output, other dice apps.

## What Paste Table understands (copied-PDF text)

- **Rows on one line:** `1-2 Backpack`, `3 Knife`, `01-30 Cold stale air`.
- **A number or range on its own line, followed by a paragraph:** the paragraph (wrapped lines are joined, blank lines
  before it are ignored) becomes that row's result, until the next number or range starts. A second paragraph after a
  blank line is kept but flagged (a page header or footer may have slipped in).
- **Result set headings:** `AMBIENT`, `NOISE`, … followed by rows start separate result sets.
- **Continuation columns** (one table printed in two or more page columns, `01-02 Ael 51-52 Wulf`): joined into one result
  set in numeric order, even when extraction squeezed the columns down to single spaces, provided the whole block shows each
  column continuing exactly where the one before it ends. Spans may be written `01-02` or `01 - 02`. Single values
  (`1 Ael 11 Wulf`) are only read as columns with extra proof — every value followed by the same separator (`1 – Ael`,
  `1. Ael`), or the heading printed once per column (`D20 RESULT D20 RESULT`) — and the columns must then cover the whole
  dice range exactly once. A d66 table steps through legal d66 values only. A line that looks like side-by-side columns
  without that proof is kept as one entry and flagged.
- **Parallel output columns** (`D8 DIFFICULTY MODIFIER` with rows such as `1 Child's play +30`): each output becomes its own
  result set sharing the row's roll, when every row splits the same way (by column gaps, or a consistent trailing `+30`/`-10`/`2d6`
  field matching two heading words). When the heading has more words than columns (`D100 WIND TYPE STRENGTH HULL DAMAGE CAUSED`),
  rows still split if two or more trailing columns each hold one recognisable shape all the way down (signed numbers, dice, or
  plain numbers beside one of those; a lone `–` may stand for "none"). The result sets are then left unnamed — name each one in
  the box above its column on the Review screen.
- **Repeated headings** (`D100 SYLLABLE D100 SYLLABLE`) are read as one heading.

Anything the parser is not sure about is kept together and flagged on the Review screen: information (blue) for
things it did and you may want to glance at, warnings (amber) for things to check.

## Cleaning up imperfect PDF copies (Review screen)

A small toolbar above the rows offers deterministic, reversible fixes for the kind of damage a PDF copy leaves behind,
so correcting it takes a click (or **Ctrl+J**) instead of retyping:

- **Join With Previous Row** (**Ctrl+J**): joins the selected row into the row above it in the same result set — the
  shape a wrapped line leaves when the parser could not safely join it on its own. Refuses, with a message, if the
  selected row has its own range (it is very likely a real row) or is the first row of its set.
- **Dehyphenate Selected Row**: removes a PDF line-wrap hyphen from the selected row's result text
  (`magnifi- cent` → `magnificent`). Ordinary hyphenated words (`well-made`, `half-orc`) are never touched — a
  line-wrap hyphen always has whitespace after it, a real one never does.
- **Remove Empty Rows**: removes rows in the current result set with no range and no result text (whitespace-only
  counts as empty). A row with either one is left alone.
- **Normalize Text**: fixes non-breaking spaces, repeated whitespace and the `fi`/`fl` ligatures across the whole
  table, and tidies a range field's separator (`1 – 2`, `1 − 2` → `1-2`). It never touches punctuation or wording in
  result prose, and never alters dice text embedded in a row (`4D6+5` stays `4D6+5`).
- **Undo Last Cleanup**: reverses the single most recent cleanup action above. Only one level deep — Review has no
  general undo/redo history, so this is the smallest safe net rather than a full command stack.

A Unicode replacement character (`�`) left by a broken PDF font mapping is flagged on its row automatically, as a
warning to review by hand; TableForge never guesses what the missing character was or silently changes it.

## Cleaning up imperfect PDF copies (Paste Table screen)

Some malformed continuation lines never reach Review at all — the parser reads them as something else before you get
there. A small set of raw-text tools sits above the paste box for exactly that, so obvious clipboard damage can be
repaired before Interpret runs, not only after:

- **Ctrl+J**: joins the line the caret is on into the line above it — trims trailing whitespace off the line above,
  leading whitespace off the current line, and joins them with exactly one space. Makes no judgement about whether the
  current line is really a continuation; it is a raw-text edit you asked for, on the line you put the caret on. Does
  nothing (with a status message) if the caret is on the first line.
- **Ctrl+J with several lines selected** (or **Join Lines**): joins every line the selection touches into one line in a
  single step — each line trimmed, blank lines dropped, the rest joined with exactly one space. It works on whole lines:
  if the selection starts or ends partway through a line, that entire line is joined, so nothing unselected on it is
  lost. A selection ending at the very start of a line (having taken only the line break before it) leaves that line
  alone. The join is literal: `well-` + `made` becomes `well- made` (use Dehyphenate for that), and `�` is never
  changed. One **Ctrl+Z** undoes the whole join. With no selection, or a selection within one line, Ctrl+J and Join
  Lines do the single-line join above.
- **Normalize Text**: the same non-breaking-space, repeated-whitespace and `fi`/`fl` ligature fixes as Review's
  Normalize Text, applied one line at a time so every line break is kept exactly as it was.
- **Dehyphenate**: removes PDF line-wrap hyphens from the raw text (`magnifi-` + a line break + `cent` becomes
  `magnificent`), including across the line's own line break. Ordinary hyphenated words (`well-made`, `half-orc`) are
  never touched.
- **Find Next Damaged Character**: only shown while the pasted text contains `�`; moves the selection to the next one
  (wrapping back to the first once it reaches the end).

All three edits go through the paste box's own text editing, so **Ctrl+Z** undoes them exactly as it would undo typing.
A warning above the box reports how many `�` characters are still in the text; as on Review, TableForge never guesses
what they should be or changes them for you.


---

# For developers

## Building and testing (developers)

Requires the .NET 10 SDK on Windows.

```
dotnet build
dotnet test
```

The test suite uses only temporary SQLite files and never touches the real application data.

## Producing the release (self-contained Windows x64)

From the repository root:

```
.\publish.ps1
```

This is the one supported way to cut a release build. It runs the full test suite in Release configuration first
(retrying once on failure, since the UI tests drive real WPF windows and are occasionally timing-flaky, but two
failures in a row stop the script), then **deletes and recreates** `publish\win-x64\` from scratch — it never
layers a new publish over old files — and finally checks that the published exe's own file version matches the
`<Version>` in `TableForge.csproj`, so a stale or partial publish is caught immediately instead of being shipped.
Pass `-SkipTests` only for iterating on the script itself; never to produce a build you intend to ship.

Output folder: `publish\win-x64\` (about 140 MB, roughly 400 files). Run `publish\win-x64\TableForge.exe`. The target machine
does **not** need .NET installed. The folder never contains debug symbols (`.pdb`): the publish profile turns them off and the
script refuses to finish if any appear.

The publish settings live in `TableForge\Properties\PublishProfiles\win-x64-folder.pubxml`: self-contained, `win-x64`, no
single-file, no trimming, no ReadyToRun. There is no auto-update in V1.

### The installer and release artifacts

```
.\publish.ps1 -Installer
```

does everything above and then builds the public release artifacts in `publish\release\`: the installer
(`installer\TableForge.iss`, Inno Setup 6; install it once with `winget install JRSoftware.InnoSetup`),
`TableForge-<version>-Setup.exe`, the same files as a portable `TableForge-<version>-win-x64.zip`, and `SHA256SUMS.txt`. The script's `AppId` must never
change: it is how a newer installer recognises and replaces an installed TableForge. The installer is the one way TableForge
is meant to be installed; the zip is for people who prefer to run it from a folder (it uses the same data folder).

Equivalent manual command, if you need it (`publish.ps1` just wraps this with the test gate and a clean folder):

```
dotnet publish TableForge\TableForge.csproj -p:PublishProfile=win-x64-folder
```

### Confirming the running exe matches current source

The bottom-right of the main window shows the running build's version and the exact time its main assembly was
written to disk, e.g. `1.0.0-rc18 · built 2026-09-22 09:41 local` (`TableForge/AppInfo.cs`). The build time comes
from the DLL's own file timestamp, not a manually-maintained field, so it stays accurate without anyone
remembering to bump it. To check a specific exe from the command line instead:

```
(Get-Item publish\win-x64\TableForge.exe).VersionInfo.ProductVersion
(Get-Item publish\win-x64\TableForge.dll).LastWriteTime
```

If the version shown doesn't match `TableForge.csproj`'s `<Version>`, or the build time is older than your last
accepted change, the running exe is stale — run `.\publish.ps1` again.

## Developer tools

**Separate data folder.** Set the environment variable `TABLEFORGE_DATA_DIR` to a folder to make TableForge keep *all* of its data
(database, dice choice, WebView2 profile) there instead of in `%LOCALAPPDATA%\TableForge`. It is meant for testing a build without
touching your real tables; leave it unset normally.

**Clipboard diagnostic.** To see which formats a program puts on the clipboard when you copy from it, and whether bold or
italic survives, copy some text in that program (for example from a PDF in PDF-XChange Editor), then run:

```
TableForge.exe --clipboard-diagnostic
```

A window lists every clipboard format present (and which program owns it), what Paste Table would receive (always plain
text), whether bold/italic exists in HTML or RTF, and whether the copy carries more line/column structure than plain text.
For a report file instead of a window: `TableForge.exe --clipboard-diagnostic --out report.txt`. It only reads the
clipboard and never opens your tables.
