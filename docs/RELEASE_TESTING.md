# TableForge release testing checklist

For testing a packaged release candidate (`TableForge-<version>-Setup.exe`) on a Windows 10/11 x64 machine, ideally one
that has never had TableForge on it. Tick each item; note anything unexpected with the TableForge version (About, or the
bottom right of the window) and your Windows version.

Your data lives in `%LOCALAPPDATA%\TableForge`. Nothing here should ever delete it unless a step says so.

## Fresh install

- [ ] The download matches `SHA256SUMS.txt` (`Get-FileHash .\TableForge-<version>-Setup.exe`).
- [ ] The installer runs (SmartScreen may warn: More info → Run anyway) and asks for **no administrator permission**.
- [ ] **TableForge** is in the Start Menu, with the TableForge icon, and starts from there.
- [ ] First start shows the welcome text; the window, taskbar and About show the TableForge icon.
- [ ] Create a Collection.
- [ ] Paste a table (copied from a PDF or web page), Interpret, correct a row, Save Table.
- [ ] Roll it with **Built-in** dice.
- [ ] Copy Table Text, paste into Notepad: name, dice, blank line, one row per line.
- [ ] Roll screen → **Export…** → **Copy Table Text (Spaces)** (directly below Copy Table Text), paste into Notepad: the
      same text, with one space instead of the tab between each range and its result.
- [ ] Close and restart TableForge: the collection and table are still there.
- [ ] About shows the right version and RPG Frequencies; License, Privacy and Open Data Folder open.

## Extended and open-ended ranges

Paste this `d20` table (Paste Table → Interpret):

```
D20 Injury
-10-0 The character is dead.
1-5 Succumbs to wounds.
6-10 Severely wounded.
11-15 A week of rest.
16-20 In shock, but alive.
21-25 Knocked out.
26+ Recovers immediately.
```

- [ ] Review shows seven rows with the ranges as written (`-10–0`, `26+`), no warnings, and information notes for the
      `-10–0`, `21–25` and `26+` rows ("outside the natural d20 range").
- [ ] Save, then roll with a situational modifier of `-10` until a roll resolves to "The character is dead."; and with
      `+10` until one resolves to "Recovers immediately." The Roll screen and Recent rolls never show a number like
      2147483647 or -2147483648.
- [ ] Type a roll of `-5` and of `40` by hand: both are accepted and find the right rows.
- [ ] **Export…** → **Copy Table Text** and **Copy for Sojour**: the ranges read `-10-0` and `26+`.
- [ ] **Export…** → **Copy Foundry JSON** and **Copy Tables+ JSON**: both refuse, naming the `26+` range. Change `26+`
      to `26-30` on Review / Edit, save, and both export.
- [ ] A `d66` table with a `61+` row: Review says open-ended ranges are not available for d66, and it cannot be saved.

## Bold and italic result text

Paste this `d6` table (Paste Table → Interpret):

```
D6 Boons
1-2 The creature gains +2 Armor until the next dawn.
3-4 You gain +1d4 Armor until next dawn.
5-6 Nothing happens.
```

- [ ] Row 1: select `+2 Armor` and press **Ctrl+B**; select `next dawn` and click **I**. The words stay selected after
      the click, the text box still shows plain text, and a preview under the row shows **+2 Armor** and *next dawn*.
      Row 3 has no preview.
- [ ] Row 2: select `+1d4 Armor` and apply both **B** and **I**. Select the same words and press **Ctrl+Space**: the
      formatting goes; apply bold and italic again.
- [ ] Type inside the bold words (`+2 Heavy Armor`): the new word is bold. Type an `s` right after `Armor`: it is not.
- [ ] **Normalize Text** and **Undo Last Cleanup** leave the formatting on the same words.
- [ ] Save, roll `1` (type it): the result shows **+2 Armor** and *next dawn*; the table's rows show it too.
- [ ] Roll `3`, then **Roll d4**: `Resolved:` shows the number bold and italic, with *next dawn* italic.
- [ ] Recent rolls, **Copy Table Text**, **Copy Table Text (Spaces)**, **Copy for Sojour**, **Copy Foundry JSON** and
      **Copy Tables+ JSON** (paste each into Notepad): plain text only, with no `*`, `<b>` or other marks.
- [ ] Edit the table again: the formatting is still there. Close and restart TableForge: still there.
- [ ] Paste a two-column table such as `D3 Difficulty` / `1 Easy +10` / `2 Normal +0` / `3 Hard -10`, Interpret, and type
      several characters into one of the aligned cells, then into a range (add a space after `2`): the cursor stays
      where you are typing the whole time.

## Table description

Use a table that links to another table (or make one: a row whose text names another table, linked on Review / Edit).

- [ ] **Edit table**: a **Description (optional)** box sits between the name / dice / folder row and the Clamp checkbox,
      about three lines high. Type two lines (**Enter** makes a new line, it does not save) and **Save Table**.
- [ ] The Roll screen shows both lines under the table's dice, in plain grey text with no label.
- [ ] A table without a description shows nothing there: no empty line or gap before "Not rolled yet."
- [ ] Give the linked-to table its own description. Roll (or type) the linking row and open the link: each step in the
      trail shows only its own table's description.
- [ ] The table list, Recent and Recent rolls (and its tooltip) never show description text.
- [ ] Paste more than 2,000 characters into the box: only the first 2,000 are kept. Save: the long text wraps under the
      dice and **Roll** stays usable.
- [ ] **Export…** → **Copy Table Text**, **Copy Table Text (Spaces)**, **Copy for Sojour**, **Copy Foundry JSON** and
      **Copy Tables+ JSON** (paste each into Notepad): none contains the description.
- [ ] Close and restart TableForge: the descriptions are still there.

## Situational modifier entry

On a `d20` table, with the **Modifier** box showing `0`:

- [ ] Open the table (focus is on **Roll**), press **Tab**: the `0` in the Modifier box is selected. Type `+2`: the box
      shows `+2` (never `+20`). Press **Enter**: the roll shows "… +2 situational" and the box is back to a selected `0`.
- [ ] Without deleting anything, type `-1`: the box shows `-1` (never `-10`). **Enter** rolls with "… -1 situational".
- [ ] Click into the "or enter a roll" box, then **Shift+Tab**: the `0` is selected again.
- [ ] Click into the "or enter a roll" box, then click once into the Modifier box: the `0` is selected; typing `3` gives
      `3`. Click again just left of the `3`: a normal cursor appears there, and typing `1` gives `13`.

## Portable Collections

Use a collection with folders, a linked table and some bold or italic text (the Boons table above works), and **no**
table descriptions.

- [ ] Select it and choose **Export Collection…**: the Save dialog offers `<collection name>.tfcollection`. Save it
      somewhere outside the data folder. Open the file in Notepad: readable text starting with
      `"format": "TableForgeCollection"` and `"formatVersion": 1`, with no `"description"`, and `"generator"` shows this
      version.
- [ ] **Import Collection…** and choose that file: before anything changes it asks to import `<name> (2)`, with the
      right folder and table counts and "Existing Collections will not be changed." Choose **Cancel**: nothing is added.
- [ ] Import it again and choose **OK**: `<name> (2)` is added and selected. Its tables roll exactly like the
      originals, the bold and italic text is still there, and following a link opens the table in `<name> (2)`.
- [ ] Import it once more: `<name> (3)`. Close and restart TableForge: both copies are still there.
- [ ] Copy the file, open the copy in Notepad and change `"formatVersion": 1` to `3`. Importing it is refused ("newer
      file format … Nothing was changed.") and the collection list is unchanged.
- [ ] Give one table in the collection a description and export again: the file says `"formatVersion": 2`, and only that
      table has a `"description"`. Import it: the copy shows the description under that table's dice.
- [ ] With rc25 (see Upgrade): the version 1 file imports; the version 2 file is refused ("uses a newer file format …
      Nothing was changed.") and rc25's collection list is unchanged.
- [ ] Select `<name> (3)` and choose **Delete Collection…**: it names the collection and its table count, says its
      folders are deleted too and that this cannot be undone. **No** keeps it; **Yes** removes it and selects the next
      collection. The original and `<name> (2)` are unchanged.

## dddice

- [ ] Choose **dddice** (guest): the dice panel prepares and a roll shows 3D dice and the right result.
- [ ] Optional, with a dddice account: Account… → Connect, approve on dddice.com, choose a theme, roll with it.
- [ ] Restart: the account and theme are remembered.
- [ ] With the network off (or dddice failing), **Use Built-in Dice** still works and rolls normally.

## Foundry VTT export

In Foundry VTT v13 or v14 with the **Roll Table Importer** module enabled (Roll Tables → Import Tables). For each table,
export from TableForge (Roll screen → **Export…**) and import into Foundry, then roll it there a few times.

- [ ] A `d20` table, pasted with **Copy Foundry JSON**.
- [ ] The same table imported from a file saved with **Save Foundry JSON…**.
- [ ] A `2d6+1` table: the formula is `2d6+1` and rolls land on the right rows.
- [ ] A `d100` table with a `96–00` row: the row is 96–100 and a roll of 100 finds it.
- [ ] A `d66` table: the formula is `1d6 * 10 + 1d6` and rolls only ever give 11–16, 21–26 … 61–66 (never 7, 10, 17 or 37).
- [ ] A `1d66` table (a real 66-sided die): the formula is `1d66`.
- [ ] Result text with curly quotes, em dashes and other Unicode.
- [ ] Result text with `&` (`Sword & Shield`).
- [ ] Result text with `<` and `>` (`Value < 10`).
- [ ] Result text on two lines.
- [ ] A table with a gap (rows 1–4 and 7–10): rolls of 5–6 find nothing, as expected.
- [ ] A table with overlapping rows, if Foundry's behavior is sensible.
- [ ] Note anything Foundry shows differently from TableForge (especially `&`, `<`, `>` and line breaks).

## Sojour

In Sojour, create or open a Lookup Table, select its first cell, then paste with **Ctrl+V** after each TableForge
**Export…** → **Copy for Sojour** (Roll screen).

- [ ] A `d6` table: each row fills two cells (range, result) and nothing else is pasted above the first row.
- [ ] A `d100` table with a `96–00` row: the range shows as `96-00`.
- [ ] A table with several result sets: only the chosen one is pasted.
- [ ] Result text with commas, curly quotes, em dashes and other Unicode.
- [ ] Result text on two lines in TableForge: it pastes as one row, with a space where the line break was.
- [ ] Result text with inline dice (`2d6 Skeletons`): pasted as written.

## Owlbear Rodeo (Tables+)

**Status (2026-09-27):** TableForge-exported `d20`, `d66` (`T66`, in the sample tested) and unmodified `2d6` JSON imported
and rolled correctly in Tables+. A separate, hand-written `1d4+20` Tables+ table consistently rolled 21–24, so Tables+
applies a positive single-die modifier (not an end-to-end TableForge export test). Everything else below is still untested;
keep re-checking the tested items in each release. Needs Owlbear Rodeo and the Tables+ extension. Import each
file from `docs/external-tests/tables-plus/` (or export the same kind of table with **Export…** → **Copy Tables+ JSON** or
**Save Tables+ JSON…**), then roll each imported table several times. For every item, note whether the import worked,
what Tables+ shows, and any roll that lands on the wrong row or on nothing.

- [ ] `d20.json`: imports as a `1d20` table and rolls land on the right rows.
- [ ] `d100.json`: the last row is 96–100 and a roll of 100 finds it.
- [ ] `genuine-1d66.json`: a single 66-sided die (`1d66`), results 1–66.
- [ ] `d66-T66.json`: `T66` rolls only 11–16, 21–26 … 61–66 (never 17, 20 or 37) and each lands on its row.
- [ ] `2d6.json`: imports as `bell-curve` `2d6`, results 2–12.
- [ ] `d20-plus-2.json`: `1d20+2` is accepted and rolls 3–22 land on the right rows (Tables+ applied `1d4+20` in a
      hand-written test; this TableForge export itself is untested).
- [ ] `d4-minus-2-signed.json`: whether `1d4-2` and the negative range (-1 to 0) are accepted, and how they roll.
- [ ] `gaps-overlaps.json`: whether rows with a gap (5–6) and an overlap (3–4) import, and what rolls in them do.
- [ ] `text-inline-2d6.json`: whether `{2d6} goblins` shows as written or rolls the dice.
- [ ] `text-reroll.json`: whether `#reroll` shows as written or makes Tables+ roll again.
- [ ] Copy vs Save: pasting and importing a file give the same table.
- [ ] Future compatibility only (TableForge refuses it today): does a hand-made Tables+ table with `2d6+1` dice import and
      roll 3–13 correctly?

## Uninstall and reinstall

- [ ] Uninstall TableForge (Settings → Apps).
- [ ] The program and its Start Menu entry are gone.
- [ ] `%LOCALAPPDATA%\TableForge` is **still there**, with `tableforge.db` in it.
- [ ] Reinstall: the earlier collection and table come back by themselves.

## Upgrade

- [ ] Install an **older** packaged release candidate.
- [ ] Create a collection and a table, roll it (and connect dddice if you use it).
- [ ] Close TableForge, then run the **newer** installer over it.
- [ ] The newer version starts (About shows it), and everything from before is still there.
- [ ] If the newer version changed the data format, a `tableforge.pre-v*-from-v*.backup.db` copy appears in the data folder.
- [ ] From rc23 to rc24: tables look and roll exactly as before, `tableforge.pre-v9-from-v8.backup.db` appears, and rc23
      then refuses the data (it was made by a newer version).
- [ ] From rc24 to rc25: no data-format change, so no new backup copy appears; tables, formatting and rolls are exactly
      as before, and the Export / Import / Delete Collection buttons are there. rc24 can still open the data afterwards.
- [ ] From rc25 to rc26: `tableforge.pre-v10-from-v9.backup.db` appears; every collection, table, formatting, link and
      Clamp setting is exactly as before, and no table has a description yet. rc25 then refuses the data (it was made
      by a newer version) and changes nothing.
