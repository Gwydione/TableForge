# TableForge — RPG Rollable Tables

**Version 1.0.0-rc1 (V1 Release Candidate 1)**

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
dotnet publish TableForge\TableForge.csproj -p:PublishProfile=win-x64-folder
```

Output folder: `publish\win-x64\` (about 140 MB, roughly 400 files). Zip that folder to distribute it. Run
`publish\win-x64\TableForge.exe`. The target machine does **not** need .NET installed.

The publish settings live in `TableForge\Properties\PublishProfiles\win-x64-folder.pubxml`: self-contained, `win-x64`, no
single-file, no trimming, no ReadyToRun. There is no installer and no auto-update in V1.

## Where your data is kept

One SQLite database file:

```
%LOCALAPPDATA%\TableForge\tableforge.db
```

(typically `C:\Users\<you>\AppData\Local\TableForge\tableforge.db`). It holds your collections, tables, recent tables and
recent rolls. The location is fixed in V1. To back up your data, copy that file while TableForge is closed; to start fresh,
delete it (or the whole `TableForge` folder there).

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
adds the rows.

**Recent** (under the search box) lists the tables you opened most recently in the current collection. **Recent rolls**
(bottom left) shows the last 10 rolls anywhere as read-only snapshots; clicking one reopens its table (a snapshot of a
deleted table stays readable but cannot be opened).

## Known limitations in this release candidate

- Only one copy of TableForge should be open at a time.
- Closing the window discards unsaved Review edits without asking.
- The parser is deliberately conservative: anything it is unsure about stays together and is flagged on the Review screen
  instead of being guessed. Side-by-side columns that each restart at the same number (parallel result sets) are not split.
- No installer, auto-update, cloud sync, export, or OCR/screenshot import.
