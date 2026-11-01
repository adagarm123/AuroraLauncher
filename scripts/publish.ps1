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

# ---- [2/4] publish ----
Write-Host "=== [2/4] dotnet publish ==="
dotnet publish $project -c Release -r win-x64 --self-contained true `
    -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true `
    -p:EnableCompressionInSingleFile=true -o $publishDir
if ($LASTEXITCODE -ne 0) {
    throw "dotnet publish failed. NOTE: if 'AuroraLauncher.exe' is locked, the launcher is still running - close it and retry."
}

$exe = Join-Path $publishDir "AuroraLauncher.exe"
if (-not (Test-Path $exe)) { throw "AuroraLauncher.exe not found in $publishDir" }

# ---- [3/4] staleness check (warning only, non-blocking) ----
Write-Host "=== [3/4] Source-vs-EXE staleness check ==="
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

# ---- [4/4] build-info.txt ----
Write-Host "=== [4/4] build-info.txt ==="
$commit = "unknown (not a git repository)"
try { $commit = (git -C $root rev-parse HEAD 2>$null); if (-not $commit) { $commit = "unknown" } } catch { }
$dirty = ""
try { $st = (git -C $root status --porcelain 2>$null); if ($st) { $dirty = " (dirty working tree)" } } catch { }
$hash = (Get-FileHash $exe -Algorithm SHA256).Hash
$exeItem = Get-Item $exe
$lines = @(
    "build-time : $($exeItem.LastWriteTime.ToString('yyyy-MM-dd HH:mm:ss'))",
    "git-commit : $commit$dirty",
    "exe-sha256 : $hash",
    "exe-size   : $($exeItem.Length) bytes",
    "tests      : unit tests green at publish time (full matrix: TEST_RESULTS.md)"
)
Set-Content (Join-Path $publishDir "build-info.txt") ($lines -join "`r`n") -Encoding utf8
$lines | ForEach-Object { Write-Host "  $_" }
Write-Host "Publish complete -> $publishDir"

}
finally { Pop-Location }
