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
      5. With -Installer only: compiles installer\TableForge.iss with Inno Setup 6 into
         publish\installer\TableForge-<version>-Setup.exe (per-user install; see the script for its contract).

.PARAMETER SkipTests
    Skips the test run. Only for local iteration on this script itself; never use this to ship a build.

.PARAMETER Installer
    After a successful publish, also builds the per-user installer (needs Inno Setup 6:
    winget install JRSoftware.InnoSetup).

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

    $installerDir = Join-Path $root "publish\installer"
    if (Test-Path $installerDir) { Remove-Item -Recurse -Force $installerDir }
    & $iscc "/DAppVersion=$csprojVersion" (Join-Path $root "installer\TableForge.iss")
    if ($LASTEXITCODE -ne 0) {
        Fail "The Inno Setup compiler failed."
    }

    $setup = Join-Path $installerDir "TableForge-$csprojVersion-Setup.exe"
    if (-not (Test-Path $setup)) {
        Fail "The installer was not produced at $setup."
    }
    Write-Host ""
    Write-Host "Installer: $setup" -ForegroundColor Green
    Write-Host "Size:      $([math]::Round((Get-Item $setup).Length / 1MB, 1)) MB" -ForegroundColor Green
}
