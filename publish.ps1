<#
.SYNOPSIS
    Runs the full test suite and, only if it passes, produces a fresh self-contained win-x64 publish of TableForge.

.DESCRIPTION
    This is the one supported way to produce a release build. It exists because publish\win-x64 has repeatedly
    gone stale relative to source/tests (a fix landed in source and tests but was never republished). To keep
    that from happening again, this script:
      1. Runs TableForge.Tests in Release configuration (matching what ships) and stops here if it fails.
         A single flaky failure is retried once before being treated as real, since the UI tests drive real
         WPF windows and read the machine's live keyboard state, which is occasionally timing-sensitive; two
         failures in a row is treated as a real regression, not a flake.
      2. Deletes publish\win-x64 entirely and recreates it from scratch, so the output can never be a mix of
         files from different builds (dotnet publish does not clean stale files on its own).
      3. Runs the real production publish (the win-x64-folder profile: self-contained, no trimming/single-file).
      4. Reads the resulting exe's own file version and compares it with the .csproj's <Version>, so a mismatch
         (e.g. the publish silently used a different/cached build) is caught immediately rather than discovered
         later by a user running a stale exe. It also refuses to finish if any debug symbols (.pdb) were published.
      5. With -Installer only: builds the public release artifacts in publish\release\ -
         TableForge-<version>-Setup.exe (installer\TableForge.iss, Inno Setup 6; per-user install, see the script),
         TableForge-<version>-win-x64.zip (the same published files, for people who prefer no installer) and
         SHA256SUMS.txt (one "<sha256>  <file>" line per artifact).

.PARAMETER SkipTests
    Skips the test run. Only for local iteration on this script itself; never use this to ship a build.

.PARAMETER Installer
    After a successful publish, also builds the release artifacts: installer, portable zip and SHA256SUMS.txt
    (needs Inno Setup 6: winget install JRSoftware.InnoSetup).

.EXAMPLE
    .\publish.ps1

.EXAMPLE
    .\publish.ps1 -Installer
#>
[CmdletBinding()]
param(
    [switch]$SkipTests,
    [switch]$Installer
)

$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
Set-Location $root

function Step($message) {
    Write-Host ""
    Write-Host "==> $message" -ForegroundColor Cyan
}

function Fail($message) {
    Write-Host $message -ForegroundColor Red
    exit 1
}

# ---- 1. tests -------------------------------------------------------------------------------------

if ($SkipTests) {
    Write-Host "Skipping tests (-SkipTests was passed). Do not ship a build produced this way." -ForegroundColor Yellow
}
else {
    Step "Running the full test suite (Release configuration)"
    dotnet test "$root\TableForge.Tests\TableForge.Tests.csproj" -c Release
    $testsPassed = ($LASTEXITCODE -eq 0)

    if (-not $testsPassed) {
        Write-Host ""
        Write-Host "Test run failed; retrying once (the real-WPF-window UI tests are occasionally timing-flaky)." -ForegroundColor Yellow
        dotnet test "$root\TableForge.Tests\TableForge.Tests.csproj" -c Release
        $testsPassed = ($LASTEXITCODE -eq 0)
    }

    if (-not $testsPassed) {
        Fail "Tests failed twice in a row. Publish aborted: fix the failure before publishing."
    }
}

# ---- 2. clean publish output -----------------------------------------------------------------------

$publishDir = Join-Path $root "publish\win-x64"
Step "Removing existing publish output ($publishDir)"
if (Test-Path $publishDir) {
    Remove-Item -Recurse -Force $publishDir
}

# ---- 3. production publish -------------------------------------------------------------------------

Step "Publishing TableForge (self-contained win-x64)"
dotnet publish "$root\TableForge\TableForge.csproj" -p:PublishProfile=win-x64-folder
if ($LASTEXITCODE -ne 0) {
    Fail "dotnet publish failed."
}

# ---- 4. verify the output ---------------------------------------------------------------------------

$exePath = Join-Path $publishDir "TableForge.exe"
if (-not (Test-Path $exePath)) {
    Fail "Publish did not produce $exePath."
}

$exeVersion = (Get-Item $exePath).VersionInfo.ProductVersion
$csprojPath = Join-Path $root "TableForge\TableForge.csproj"
$csprojVersion = ([xml](Get-Content $csprojPath -Raw)).Project.PropertyGroup.Version | Where-Object { $_ } | Select-Object -First 1

if ($exeVersion -ne $csprojVersion) {
    Fail "Version mismatch: published exe reports '$exeVersion' but TableForge.csproj says '$csprojVersion'. The publish did not pick up current source; investigate before shipping this build."
}

$symbols = @(Get-ChildItem $publishDir -Recurse -Filter *.pdb)
if ($symbols.Count -gt 0) {
    Fail "Debug symbols were published ($($symbols.Name -join ', ')). The public build must not contain .pdb files; check the win-x64-folder publish profile."
}

# Nothing for developers only: IntelliSense files, test assemblies, databases, logs, scratch or spike files.
$devOnly = @(Get-ChildItem $publishDir -Recurse -File | Where-Object {
    $_.Extension -in '.xml', '.db', '.db-wal', '.db-shm', '.log', '.tmp' -or $_.Name -like '*Tests*' -or $_.Name -like '*Spike*' -or $_.Name -like 'roll-timing*'
})
if ($devOnly.Count -gt 0) {
    Fail "Development-only files were published ($($devOnly.Name -join ', ')). Remove them from the public build."
}

Write-Host ""
Write-Host "Published $exePath" -ForegroundColor Green
Write-Host "Version:   $exeVersion (matches TableForge.csproj)" -ForegroundColor Green
Write-Host "Built:     $((Get-Item (Join-Path $publishDir 'TableForge.dll')).LastWriteTime)" -ForegroundColor Green

# ---- 5. optional installer --------------------------------------------------------------------------

if ($Installer) {
    Step "Building the installer (Inno Setup)"
    $iscc = @(
        (Join-Path $env:LOCALAPPDATA "Programs\Inno Setup 6\ISCC.exe"),
        (Join-Path ${env:ProgramFiles(x86)} "Inno Setup 6\ISCC.exe"),
        (Join-Path $env:ProgramFiles "Inno Setup 6\ISCC.exe")
    ) | Where-Object { $_ -and (Test-Path $_) } | Select-Object -First 1
    if (-not $iscc) {
        Fail "Inno Setup 6 was not found. Install it with: winget install JRSoftware.InnoSetup"
    }

    $releaseDir = Join-Path $root "publish\release"
    if (Test-Path $releaseDir) { Remove-Item -Recurse -Force $releaseDir }
    foreach ($old in @((Join-Path $root "publish\installer"))) { if (Test-Path $old) { Remove-Item -Recurse -Force $old } } # pre-RC18 location
    & $iscc "/DAppVersion=$csprojVersion" (Join-Path $root "installer\TableForge.iss")
    if ($LASTEXITCODE -ne 0) {
        Fail "The Inno Setup compiler failed."
    }

    $setup = Join-Path $releaseDir "TableForge-$csprojVersion-Setup.exe"
    if (-not (Test-Path $setup)) {
        Fail "The installer was not produced at $setup."
    }

    Step "Building the portable zip"
    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $zip = Join-Path $releaseDir "TableForge-$csprojVersion-win-x64.zip"
    # The published files at the zip's root: "Extract All" puts them in a folder named after the zip. Each file is added
    # by hand so every entry name uses "/" as the zip format requires (Windows PowerShell's CreateFromDirectory writes "\",
    # which other unzip tools turn into odd file names).
    $publishRoot = (Resolve-Path $publishDir).Path.TrimEnd('\')
    $archive = [System.IO.Compression.ZipFile]::Open($zip, [System.IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $publishRoot -Recurse -File) {
            $entryName = $file.FullName.Substring($publishRoot.Length + 1).Replace('\', '/')
            [void][System.IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [System.IO.Compression.CompressionLevel]::Optimal)
        }
    }
    finally { $archive.Dispose() }
    if (-not (Test-Path $zip)) {
        Fail "The zip was not produced at $zip."
    }
    $zipRead = [System.IO.Compression.ZipFile]::OpenRead($zip)
    try { $zipEntries = @($zipRead.Entries | ForEach-Object { $_.FullName }) } finally { $zipRead.Dispose() }
    $publishedCount = @(Get-ChildItem $publishRoot -Recurse -File).Count
    if ($zipEntries.Count -ne $publishedCount -or @($zipEntries | Where-Object { $_.Contains('\') }).Count -gt 0) {
        Fail "The zip does not match the published files ($($zipEntries.Count) entries for $publishedCount files, or entries using '\')."
    }

    Step "Writing SHA256SUMS.txt"
    $sums = Join-Path $releaseDir "SHA256SUMS.txt"
    $lines = foreach ($artifact in @($setup, $zip)) {
        "$((Get-FileHash $artifact -Algorithm SHA256).Hash.ToLowerInvariant())  $(Split-Path $artifact -Leaf)"
    }
    [System.IO.File]::WriteAllText($sums, (($lines -join "`n") + "`n"), (New-Object System.Text.UTF8Encoding($false)))

    Write-Host ""
    Write-Host "Release artifacts in $releaseDir" -ForegroundColor Green
    Write-Host "Installer: $(Split-Path $setup -Leaf) ($([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB)" -ForegroundColor Green
    Write-Host "Zip:       $(Split-Path $zip -Leaf) ($([math]::Round((Get-Item $zip).Length / 1MB, 1)) MB)" -ForegroundColor Green
    Get-Content $sums | ForEach-Object { Write-Host "SHA-256:   $_" -ForegroundColor Green }
}
