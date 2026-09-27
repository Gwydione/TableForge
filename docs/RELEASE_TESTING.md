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
- [ ] Close and restart TableForge: the collection and table are still there.
- [ ] About shows the right version and RPG Frequencies; License, Privacy and Open Data Folder open.

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
