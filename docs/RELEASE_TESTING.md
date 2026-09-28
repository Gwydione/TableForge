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
