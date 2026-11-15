# Signs the built EXE into publish\latest.json (the auto-update manifest).
# Usage: powershell -File scripts\sign-release.ps1 -DownloadUrl "https://your-host/AuroraLauncher.exe" [-Round 57] [-NotesFile notes.txt]
#
# Requires scripts\signing\private_key.xml (kept PRIVATE - never share/commit).
# Output: publish\latest.json  -> upload it together with publish\AuroraLauncher.exe
# to your feed host (any static HTTPS host; see docs\auto-update.md).

param(
    [Parameter(Mandatory = $true)][string]$DownloadUrl,
    [int]$Round = 0,                       # 0 = read from src\AuroraCore\BuildInfo.cs
    [string]$Version = "",                 # "" = derived from the round
    [string]$Notes = "",
    [string]$NotesFile = ""
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot   # scripts\ -> repo root
$exe = Join-Path $root "publish\AuroraLauncher.exe"
$keyFile = Join-Path $PSScriptRoot "signing\private_key.xml"
if (-not (Test-Path $exe)) { throw "publish\AuroraLauncher.exe not found - run scripts\publish.ps1 first." }

# ========= 修改：优先从环境变量读取私钥 =========
$privateKeyXml = $null
if ($env:RSA_PRIVATE_KEY) {
    $privateKeyXml = $env:RSA_PRIVATE_KEY
} elseif (Test-Path $keyFile) {
    $privateKeyXml = Get-Content $keyFile -Raw
} else {
    throw "找不到私钥！既没有 RSA_PRIVATE_KEY 环境变量，也没有找到 $keyFile"
}
# =================================================
if ($Round -le 0) {
    $src = Get-Content (Join-Path $root "src\AuroraCore\BuildInfo.cs") -Raw
    if ($src -match 'CurrentRound\s*=\s*(\d+)') { $Round = [int]$Matches[1] }
    else { throw "Cannot read CurrentRound from BuildInfo.cs - pass -Round explicitly." }
}
if (-not $Version) { $Version = "0.$Round.0" }
if ($NotesFile -and (Test-Path $NotesFile)) { $Notes = (Get-Content $NotesFile -Raw) }

$exeItem = Get-Item $exe
$sha = (Get-FileHash $exe -Algorithm SHA256).Hash
$payload = "$Round|$Version|$DownloadUrl|$sha|$($exeItem.Length)"

$rsa = [System.Security.Cryptography.RSA]::Create()
$rsa.FromXmlString($privateKeyXml)
$sig = [Convert]::ToBase64String($rsa.SignData([Text.Encoding]::UTF8.GetBytes($payload),
    [System.Security.Cryptography.HashAlgorithmName]::SHA256,
    [System.Security.Cryptography.RSASignaturePadding]::Pkcs1))

# ---- round-74: optional delta patch published next to the EXE ----
# publish.ps1 writes patch-info.txt only when a patch against the previous build
# was small enough to be worth shipping; its absence simply means "full package".
$patchFile = Join-Path $root "publish\AuroraLauncher.patch"
$patchInfo = Join-Path $root "publish\patch-info.txt"
$patchUrl = ""
$patchSha = ""
$patchSize = 0
$patchFrom = 0
$patchSig = ""
if ((Test-Path $patchFile) -and (Test-Path $patchInfo)) {
    $pi = Get-Content $patchInfo -Raw
    if ($pi -match 'from-round\s*:\s*(\d+)') { $patchFrom = [int]$Matches[1] }
    $patchSha = (Get-FileHash $patchFile -Algorithm SHA256).Hash
    $patchSize = (Get-Item $patchFile).Length
    $patchUrl = $DownloadUrl -replace 'AuroraLauncher\.exe$', 'AuroraLauncher.patch'
    if ($patchUrl -eq $DownloadUrl) { $patchUrl = "$DownloadUrl.patch" }
    $patchPayload = "$Round|$patchUrl|$patchSha|$patchSize"
    $patchSig = [Convert]::ToBase64String($rsa.SignData([Text.Encoding]::UTF8.GetBytes($patchPayload),
        [System.Security.Cryptography.HashAlgorithmName]::SHA256,
        [System.Security.Cryptography.RSASignaturePadding]::Pkcs1))
    Write-Host "Patch: $patchSize bytes (from round $patchFrom) -> $patchUrl"
}
else {
    Write-Host "No patch for this release - clients download the full package."
}

$manifest = [ordered]@{
    round       = $Round
    version     = $Version
    releaseTime = $exeItem.LastWriteTime.ToString("yyyy-MM-dd HH:mm:ss")
    notes       = $Notes
    url         = $DownloadUrl
    sha256      = $sha
    size        = $exeItem.Length
    signature   = $sig
}
if ($patchUrl) {
    $manifest["patchUrl"] = $patchUrl
    $manifest["patchSha256"] = $patchSha
    $manifest["patchSize"] = $patchSize
    $manifest["patchFromRound"] = $patchFrom
    $manifest["patchSignature"] = $patchSig
}
$json = ($manifest | ConvertTo-Json)
Set-Content (Join-Path $root "publish\latest.json") $json -Encoding utf8
Write-Host "latest.json written:"
Write-Host $json
Write-Host ""
Write-Host "Signed manifest ready for EXE at: $DownloadUrl"
Write-Host ""
Write-Host "On a release tag, .github/workflows/release.yml uploads EXE + build-info.txt +"
Write-Host "latest.json and deploys publish/ to Pages atomically - no manual upload needed."
Write-Host "Local/offline signing only: -DownloadUrl must match the EXE's real hosted address."
