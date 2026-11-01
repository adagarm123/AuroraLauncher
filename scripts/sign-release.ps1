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
$json = ($manifest | ConvertTo-Json)
Set-Content (Join-Path $root "publish\latest.json") $json -Encoding utf8
Write-Host "latest.json written:"
Write-Host $json
Write-Host ""
Write-Host "Upload BOTH files to your feed host (URLs must match -DownloadUrl):"
Write-Host "  publish\AuroraLauncher.exe -> $DownloadUrl"
Write-Host "  publish\latest.json        -> (your latest.json URL, set as UpdateService.DefaultFeedUrl)"
