# TableForge — RPG Rollable Tables

**Version 1.0.0-rc7 (V1 Release Candidate 7)**

TableForge is a Windows desktop app for rolling RPG tables during play. Paste a table copied from a PDF or book, correct what
the parser got wrong, save it, and roll it. Multi-column tables (several result sets from one roll) and linked tables
(“Scavenging → Scavenged Items”) are supported.

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

Output folder: `publish\win-x64\` (about 140 MB, roughly 400 files). Zip that folder to distribute it. Run
`publish\win-x64\TableForge.exe`. The target machine does **not** need .NET installed.

The publish settings live in `TableForge\Properties\PublishProfiles\win-x64-folder.pubxml`: self-contained, `win-x64`, no
single-file, no trimming, no ReadyToRun. There is no installer and no auto-update in V1.

Equivalent manual command, if you need it (`publish.ps1` just wraps this with the test gate and a clean folder):

```
dotnet publish TableForge\TableForge.csproj -p:PublishProfile=win-x64-folder
```

### Confirming the running exe matches current source

The bottom-right of the main window shows the running build's version and the exact time its main assembly was
written to disk, e.g. `1.0.0-rc7 · built 2026-09-22 09:41 local` (`TableForge/AppInfo.cs`). The build time comes
from the DLL's own file timestamp, not a manually-maintained field, so it stays accurate without anyone
remembering to bump it. To check a specific exe from the command line instead:

```
(Get-Item publish\win-x64\TableForge.exe).VersionInfo.ProductVersion
(Get-Item publish\win-x64\TableForge.dll).LastWriteTime
```

If the version shown doesn't match `TableForge.csproj`'s `<Version>`, or the build time is older than your last
accepted change, the running exe is stale — run `.\publish.ps1` again.

## Where your data is kept

One SQLite database file:

```
%LOCALAPPDATA%\TableForge\tableforge.db
```

(typically `C:\Users\<you>\AppData\Local\TableForge\tableforge.db`). It holds your collections, tables, recent tables and
recent rolls. The location is fixed in V1. To back up your data, copy that file while TableForge is closed; to start fresh,
delete it (or the whole `TableForge` folder there).

Beside it TableForge keeps `dice-provider.txt` (one word, `builtin` or `dddice`: your dice choice) and, only if you ever choose
dddice, a `WebView2` folder (the browser profile that draws the dice; it holds no tables).

On first launch TableForge creates the folder and database itself. On later launches it upgrades an older database
automatically (each upgrade is all-or-nothing, so a failed upgrade leaves your data untouched and explains what happened).

## Using it

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

## Inline dice in results

When a result's own text contains a dice expression TableForge already knows how to roll — `D20 Construction Supplies`,
`Obtain 1d6 trinkets`, `Take 2d6+1 damage`, `Roll d66 for an encounter` — a **Roll** button for that expression appears
beside it once the result is showing. The text itself is never rewritten. Pressing the button rolls through whichever
dice provider is currently selected (Built-in or dddice, with the same visible dice and the same wait for `dddice` to
settle) and shows the outcome next to the button, e.g. `d20 → 14`; pressing **Roll Again** appends further results
(`d20 → 14, 7`) instead of replacing them. A result with more than one supported expression (`Gain 1d6 coins and 1d4
gems`) offers one button per distinct expression; the same expression repeated in one result (`d6 food and d6 water`)
gets only one button. Text such as `UD6` or `4d6kh3` that only looks like dice notation is left alone: TableForge never
guesses at a second, more permissive dice grammar, only the same one a table's own dice already use.

These rolls are auxiliary: they never appear in Recent Rolls, and rolling one never re-resolves the table or follows a
linked result — a result that both contains dice text and links to another table offers both actions independently.
Inline results are transient: rolling the parent table again, or following a link, clears them; nothing is saved.

## Dice: Built-in or dddice

The **Dice** choice at the bottom of the left pane picks how tables are rolled:

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

Not included yet: connecting a dddice account (owned themes, custom dice), OBS output, other dice apps.

## What Paste Table understands (copied-PDF text)

- **Rows on one line:** `1-2 Backpack`, `3 Knife`, `01-30 Cold stale air`.
- **A number or range on its own line, followed by a paragraph:** the paragraph (wrapped lines are joined, blank lines
  before it are ignored) becomes that row's result, until the next number or range starts. A second paragraph after a
  blank line is kept but flagged (a page header or footer may have slipped in).
- **Result set headings:** `AMBIENT`, `NOISE`, … followed by rows start separate result sets.
- **Continuation columns** (one table printed in two page columns, `01-02 Ael 51-52 Wulf`): joined into one result set in
  numeric order, even when extraction squeezed the columns down to single spaces, provided the whole block shows the right
  column continuing exactly where the left one ends.
- **Parallel output columns** (`D8 DIFFICULTY MODIFIER` with rows such as `1 Child's play +30`): each output becomes its own
  result set sharing the row's roll, when every row splits the same way (by column gaps, or a consistent trailing `+30`/`-10`/`2d6`
  field matching two heading words).
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

## Known limitations in this release candidate

- Only one copy of TableForge should be open at a time.
- Closing the window discards unsaved Review edits without asking.
- The parser is deliberately conservative: anything it is unsure about stays together and is flagged on the Review screen
  instead of being guessed. Side-by-side columns that each restart at the same number (parallel result sets) are not split.
- No installer, auto-update, cloud sync, export, or OCR/screenshot import.
- dddice mode is guest-only (one free dice theme), needs internet and the WebView2 Runtime, and a roll needs the window visible.
