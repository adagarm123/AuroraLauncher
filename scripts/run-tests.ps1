# AuroraLauncher 自测脚本：构建 + 运行自测
# 默认只跑 unit（离线、毫秒级、100% 稳定）；-Full 跑全部（含网络集成测试）
# 用法: powershell -File scripts/run-tests.ps1 [-Full]
param([switch]$Full)
$ErrorActionPreference = "Stop"
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root

Write-Host "== Building AuroraLauncher.Desktop ==" -ForegroundColor Cyan
dotnet build src/AuroraLauncher.Desktop/AuroraLauncher.Desktop.csproj -c Debug --nologo -v q
if ($LASTEXITCODE -ne 0) { Write-Host "BUILD FAILED" -ForegroundColor Red; exit 1 }

$mode = if ($Full) { "--all" } else { "--unit" }
Write-Host "== Running self-tests (mode: $($mode.TrimStart('-'))) ==" -ForegroundColor Cyan
dotnet run --project tests/AuroraSelfTest -c Debug --no-build -- $mode
if ($LASTEXITCODE -ne 0) { Write-Host "TESTS FAILED" -ForegroundColor Red; exit 1 }

Write-Host "== ALL GREEN ==" -ForegroundColor Green
