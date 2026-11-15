# Publish AuroraLauncher (single-file, self-contained) and write build-info.txt.
# Usage: powershell -File scripts\publish.ps1
#
# Output in publish\:
#   AuroraLauncher.exe  - single-file self-contained build (compressed)
#   build-info.txt      - build time / git commit / exe SHA-256 / test status
#                         (answer "am I on the latest build?" by reading this)

$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$publishDir = Join-Path $root "publish"
$project = Join-Path $root "src\AuroraLauncher.Desktop\AuroraLauncher.Desktop.csproj"
$testProject = Join-Path $root "tests\AuroraSelfTest\AuroraSelfTest.csproj"

# Self-tests locate the repo root by walking up from the CURRENT directory —
# anchor the working directory to the repo regardless of where the script
# was invoked from.
Push-Location $root
try {

# ---- [1/4] unit tests must be green before publishing ----
# Deliberately --unit (offline, HARDCODED, do not override): publishing must
# never depend on network weather; the full matrix runs on CI schedule.
Write-Host "=== [1/4] Unit tests ==="
dotnet run --project $testProject -c Debug -- --unit
if ($LASTEXITCODE -ne 0) { throw "Unit tests FAILED - publish aborted." }

# ---- [1.5] archive the current EXE so a delta patch can be built ----
# Round-74: incremental updates need the PREVIOUS published build. Keeping only
# the last one is enough (users update one round at a time) and costs 65 MB.
$exe = Join-Path $publishDir "AuroraLauncher.exe"
# NOT under publish/: that folder is deployed to Pages, and a 65 MB "previous
# build" would be uploaded on every deploy for no reason.
$prevDir = Join-Path $root "artifacts\prev"
$prevExe = Join-Path $prevDir "AuroraLauncher.exe"
# `.last-round` is written at the end of every publish, so it always describes
# whatever EXE is sitting in publish\ right now (build-info.txt gained a round
# line only in round 74, hence the fallback).
$marker = Join-Path $publishDir ".last-round"
$prevRound = 0
if (Test-Path $marker) {
    $rawMarker = (Get-Content $marker -Raw).Trim()
    if ($rawMarker -match '^\d+$') { $prevRound = [int]$rawMarker }
}
if (Test-Path $exe) {
    New-Item -ItemType Directory -Force -Path $prevDir | Out-Null
    Copy-Item $exe $prevExe -Force
    $prevInfo = Join-Path $publishDir "build-info.txt"
    if (Test-Path $prevInfo) {
        Copy-Item $prevInfo (Join-Path $prevDir "build-info.txt") -Force
        if ($prevRound -eq 0) {
            $prevText = Get-Content $prevInfo -Raw
            if ($prevText -match 'round\s*:\s*(\d+)') { $prevRound = [int]$Matches[1] }
        }
    }
    Write-Host "Archived previous build (round $prevRound) -> artifacts\prev"
}

# ---- [2/5] publish ----
Write-Host "=== [2/5] dotnet publish ==="
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed. NOTE: if 'AuroraLauncher.exe' is locked, the launcher is still running - close it and retry."
}

if (-not (Test-Path $exe)) { throw "AuroraLauncher.exe not found in $publishDir" }

# ---- [3/5] staleness check (warning only, non-blocking) ----
Write-Host "=== [3/5] Source-vs-EXE staleness check ==="
$inGit = $false
try { $inGit = ((git -C $root rev-parse --is-inside-work-tree 2>$null) -eq "true") } catch { }
if (-not $inGit) {
    # zip releases: all extracted files share the archive timestamp, so a
    # file-time comparison would false-alarm. Skip gracefully (round-53).
    Write-Host "Skipping (not a git repository - zip releases have uniform timestamps)."
}
else {
    $exeTime = (Get-Item $exe).LastWriteTime
    $srcFiles = Get-ChildItem (Join-Path $root "src"), (Join-Path $root "tests") -Recurse -Include *.cs, *.xaml |
        Where-Object { $_.FullName -notmatch '\\(bin|obj)\\' }
    $newest = $srcFiles | Sort-Object LastWriteTime -Descending | Select-Object -First 1
    if ($newest -and $newest.LastWriteTime -gt $exeTime) {
        Write-Warning ("Source is NEWER than the EXE ({0} > {1}) - you may have forgotten to rebuild. Newest source file: {2}" -f `
            $newest.LastWriteTime, $exeTime, $newest.FullName)
    }
    else {
        Write-Host "OK: no source file is newer than the EXE."
    }
}

# ---- [4/5] delta patch against the archived build ----
Write-Host "=== [4/5] delta patch ==="
$patchFile = Join-Path $publishDir "AuroraLauncher.patch"
$patchInfo = Join-Path $publishDir "patch-info.txt"
Remove-Item $patchFile, $patchInfo -ErrorAction SilentlyContinue
$round = 0
$buildSrc = Get-Content (Join-Path $root "src\AuroraCore\BuildInfo.cs") -Raw
if ($buildSrc -match 'CurrentRound\s*=\s*(\d+)') { $round = [int]$Matches[1] }
if ($prevRound -gt 0 -and $prevRound -ne $round -and (Test-Path $prevExe)) {
    dotnet run --project $testProject -c Release -- --make-patch $prevExe $exe $patchFile
    if ($LASTEXITCODE -eq 0 -and (Test-Path $patchFile)) {
        $patchHash = (Get-FileHash $patchFile -Algorithm SHA256).Hash
        $patchSize = (Get-Item $patchFile).Length
        @(
            "from-round : $prevRound",
            "to-round   : $round",
            "patch-sha256 : $patchHash",
            "patch-size : $patchSize"
        ) | Set-Content $patchInfo -Encoding utf8
        Write-Host "Patch ready: $patchSize bytes (from round $prevRound)"
    }
    else {
        Write-Warning "Patch generation skipped (exit $LASTEXITCODE) - will publish the full package only."
        Remove-Item $patchFile -ErrorAction SilentlyContinue
    }
}
else {
    Write-Host "No previous build of a different round - full package only."
}

# ---- [5/5] build-info.txt ----
Write-Host "=== [5/5] build-info.txt ==="
$commit = "unknown (not a git repository)"
try { $commit = (git -C $root rev-parse HEAD 2>$null); if (-not $commit) { $commit = "unknown" } } catch { }
$dirty = ""
try { $st = (git -C $root status --porcelain 2>$null); if ($st) { $dirty = " (dirty working tree)" } } catch { }
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
$exeItem = Get-Item $exe
$patchLine = "patch      : none (full package only)"
if (Test-Path $patchInfo) {
    $pi = Get-Content $patchInfo -Raw
    if ($pi -match 'patch-size\s*:\s*(\d+)') { $patchLine = "patch      : $($Matches[1]) bytes" }
}
$lines = @(
    "build-time : $($exeItem.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))",
    "git-commit : $commit$dirty",
    "round      : $round",
    "exe-sha256 : $hash",
    "exe-size   : $($exeItem.Length) bytes",
    $patchLine,
    "tests      : unit tests green at publish time (full matrix: TEST_RESULTS.md)"
)
Set-Content (Join-Path $publishDir "build-info.txt") ($lines -join "`r`n") -Encoding utf8
$lines | ForEach-Object { Write-Host "  $_" }
Set-Content $marker $round -Encoding utf8
Write-Host "Publish complete -> $publishDir"

}
finally { Pop-Location }
