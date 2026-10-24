# Changelog

Each released version of TableForge, newest first. The full notes for each one are in `docs/release-notes/`.

## 1.1.0

- **New: PUM Companion export.**
  - **Collection:** **Export to PUM Companion…** saves the selected collection as a `.pumb` bundle.
  - **Table:** on the Roll screen, **Export…** → **Save PUM Companion Bundle…** saves the table shown.
  - **Review before saving:** a review lists what will be exported, what will be skipped and why, the warnings and the
    full report (**Copy Report**). The file is written completely or not at all.
  - **Same results:** every exported table shows the same result in PUM as in TableForge for every roll of its own dice.
    A table that can't be reproduced exactly is skipped, with the reason. This covers overlapping rows, rolls below 1
    that would change the result, text PUM would run as a command, and tables whose every result is blank.
  - **Conversions:** No Match becomes a `(No result)` row, and Clamp to Range is kept. Several result sets become one
    table, links become a `→ Table` text line, and bold and italic become Markdown. A `d66` becomes `1D66`. Descriptions
    aren't exported.
  - **Re-importing:** the review warns that importing the same bundle again lets PUM overwrite tables it already has.
- No change to rolling, to the other exports or to the data format (database schema 10, `.tfcollection` unchanged).
- Notes: `docs/release-notes/1.1.0.md`

## 1.0.0

- The first stable release, adding the Streaming Overlay (OBS) on top of the 1.0.0 release candidates:
  - Paste and Review, Collections and folders, Built-in and dddice dice;
  - linked tables, inline dice, several rolls at once;
  - table descriptions, and bold and italic results;
  - Portable Collections;
  - Copy Table Text, Copy for Sojour, and Foundry VTT and Owlbear Rodeo (Tables+) export.
- Notes: `docs/release-notes/1.0.0.md`. Notes for the release candidates are `docs/release-notes/1.0.0-rc18.md` to `1.0.0-rc26.md`.
