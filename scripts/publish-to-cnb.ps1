# Mirrors the GitHub Release assets to a CNB (cnb.cool) release via CNB OpenAPI.
# Usage: powershell -File scripts\publish-to-cnb.ps1 -Tag v1.12.22
# Requires env: CNB_TOKEN (access token), CNB_REPO (e.g. owner/AuroraLauncher).
# Idempotent: existing release/tag is reused; same-name assets are overwritten.
param(
    [Parameter(Mandatory = $true)][string]$Tag
)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
$repo = $env:CNB_REPO
$token = $env:CNB_TOKEN
if (-not $repo -or -not $token) { throw "CNB_REPO / CNB_TOKEN environment variables are required." }

$api = "https://api.cnb.cool/$repo"
$headers = @{ "Authorization" = "Bearer $token"; "Accept" = "application/vnd.cnb.api+json" }
$jsonHeaders = @{ "Authorization" = "Bearer $token"; "Accept" = "application/vnd.cnb.api+json"; "Content-Type" = "application/json" }

function Invoke-Cnb {
    param([string]$Method, [string]$Url, [hashtable]$Headers, $Body = $null)
    try {
        if ($null -ne $Body) {
            return Invoke-RestMethod -Method $Method -Uri $Url -Headers $Headers -Body ($Body | ConvertTo-Json -Compress) -TimeoutSec 120
        }
        return Invoke-RestMethod -Method $Method -Uri $Url -Headers $Headers -TimeoutSec 120
    }
    catch {
        $resp = $_.Exception.Response
        $code = if ($resp) { [int]$resp.StatusCode } else { 0 }
        return @{ __error = $true; __code = $code; __message = $_.Exception.Message }
    }
}

# 1. Reuse the release if the tag already exists, otherwise create it.
$existing = Invoke-Cnb -Method GET -Url "$api/-/releases/tags/$Tag" -Headers $headers
$releaseId = $null
if (-not $existing.__error -and $existing.id) {
    $releaseId = $existing.id
    Write-Host "CNB release for $Tag already exists (id=$releaseId); reusing."
}
else {
    $created = Invoke-Cnb -Method POST -Url "$api/-/releases" -Headers $jsonHeaders -Body @{
        tag_name = $Tag
        name = $Tag
        body = "Mirrored from GitHub Release $Tag (AuroraLauncher auto-update feed)."
        draft = $false
        prerelease = $false
        make_latest = "true"
    }
    if ($created.__error) { throw "Failed to create CNB release: $($created.__message)" }
    $releaseId = $created.id
    Write-Host "Created CNB release for $Tag (id=$releaseId)."
}

# 2. Upload every published asset (overwrite same-name files on re-runs).
$assetNames = @("AuroraLauncher.exe", "AuroraLauncher.patch", "build-info.txt", "latest.json")
foreach ($name in $assetNames) {
    $path = Join-Path $root "publish\$name"
    if (-not (Test-Path $path)) { Write-Host "  skip $name (not present)"; continue }
    $item = Get-Item $path

    $ticket = Invoke-Cnb -Method POST -Url "$api/-/releases/$releaseId/asset-upload-url" -Headers $jsonHeaders -Body @{
        asset_name = $name
        size = $item.Length
        overwrite = $true
        ttl = 0                      # 0 = permanent
    }
    if ($ticket.__error) { throw "Failed to get upload URL for $name`: $($ticket.__message)" }

    # Pre-signed PUT: no bearer header — the URL itself carries the signature.
    try {
        Invoke-RestMethod -Method PUT -Uri $ticket.upload_url -ContentType "application/octet-stream" `
            -InFile $path -TimeoutSec 300 | Out-Null
    }
    catch { throw "Failed to PUT $name to CNB: $($_.Exception.Message)" }

    # verify_url is absolute (contains upload_token / asset_path); POST it as-is.
    $verify = Invoke-RestMethod -Method POST -Uri $ticket.verify_url -Headers $headers -TimeoutSec 120
    Write-Host "  uploaded $name ($($item.Length) bytes)"
}

Write-Host "CNB mirror complete: https://cnb.cool/$repo/-/releases/download/$Tag/latest.json"
