; TableForge per-user installer (Inno Setup 6).
;
; Build it with:   .\publish.ps1 -Installer      (publishes first, then compiles this script)
; or by hand:      ISCC.exe /DAppVersion=1.0.0-rc17 installer\TableForge.iss   (after publish.ps1)
;
; Contract:
;   - Installs the self-contained publish\win-x64 folder to %LOCALAPPDATA%\Programs\TableForge, for the current Windows
;     user only, with no administrator prompt. No .NET install is needed; the WebView2 Runtime is NOT a prerequisite
;     (TableForge starts without it; only dddice dice need it).
;   - AppId below never changes: a newer installer recognises TableForge and replaces it in place.
;   - TableForge's data lives in %LOCALAPPDATA%\TableForge (database, settings, dddice account, WebView2 profile,
;     migration backups). This script never installs anything there, and neither upgrading nor uninstalling touches it:
;     uninstall removes only what was installed below (program files, Start Menu shortcut, uninstaller).

#ifndef AppVersion
  #error Pass the version: ISCC.exe /DAppVersion=<TableForge.csproj Version> installer\TableForge.iss
#endif

[Setup]
AppId={{89A2A71F-CF3F-4C91-BDCB-125354F776EB}
AppName=TableForge
AppVersion={#AppVersion}
AppVerName=TableForge {#AppVersion}
AppPublisher=RPG Frequencies
AppPublisherURL=https://github.com/Gwydione/TableForge
AppSupportURL=https://github.com/Gwydione/TableForge/issues
AppUpdatesURL=https://github.com/Gwydione/TableForge/releases
AppCopyright=Copyright (c) 2026 RPG Frequencies
VersionInfoCompany=RPG Frequencies
VersionInfoCopyright=Copyright (c) 2026 RPG Frequencies
VersionInfoProductName=TableForge
SetupIconFile=TableForge\Assets\TableForge.ico
VersionInfoVersion=1.0.0
VersionInfoProductTextVersion={#AppVersion}
PrivilegesRequired=lowest
PrivilegesRequiredOverridesAllowed=
DefaultDirName={localappdata}\Programs\TableForge
DisableDirPage=yes
DefaultGroupName=TableForge
DisableProgramGroupPage=yes
ArchitecturesAllowed=x64compatible
ArchitecturesInstallIn64BitMode=x64compatible
MinVersion=10.0
UninstallDisplayName=TableForge
UninstallDisplayIcon={app}\TableForge.exe
CloseApplications=yes
RestartApplications=no
WizardStyle=modern
Compression=lzma2
SolidCompression=yes
SourceDir=..
OutputDir=publish\release
OutputBaseFilename=TableForge-{#AppVersion}-Setup

[Files]
; The published app, exactly as publish.ps1 produced and verified it (never debug symbols). It already contains
; LICENSE.txt, THIRD-PARTY-NOTICES.txt and PRIVACY.txt beside TableForge.exe.
Source: "publish\win-x64\*"; DestDir: "{app}"; Excludes: "*.pdb"; Flags: ignoreversion recursesubdirs createallsubdirs

[Icons]
Name: "{autoprograms}\TableForge"; Filename: "{app}\TableForge.exe"; IconFilename: "{app}\TableForge.exe"

[Run]
Filename: "{app}\TableForge.exe"; Description: "Start TableForge"; Flags: nowait postinstall skipifsilent

; Deliberately no [UninstallDelete] and no [InstallDelete]: nothing outside the installed files is ever removed.
