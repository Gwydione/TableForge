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
