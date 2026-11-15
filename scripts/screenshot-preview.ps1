param(
    [string]$Out = "$env:TEMP\aurora-shot.png",
    [string]$Glass = "faux",
    [int]$Theme = 1,
    [int]$MaxWidth = 1160
)

$ErrorActionPreference = "Stop"

# DPI-aware: otherwise GetWindowRect returns virtualised (logical) pixels and the
# crop lands on the top-left quarter of the real window.
Add-Type -TypeDefinition @"
using System.Runtime.InteropServices;
public class ShotDpi {
    [DllImport("shcore.dll")] public static extern int SetProcessDpiAwareness(int value);
}
"@
try { [ShotDpi]::SetProcessDpiAwareness(2) | Out-Null } catch { }

$src = Join-Path (Split-Path -Parent $PSScriptRoot) "src\AuroraLauncher.Desktop\bin\Debug\net10.0-windows"
$run = Join-Path $env:TEMP ("aurora-shot-" + [Guid]::NewGuid().ToString("N").Substring(0, 6))
New-Item -ItemType Directory -Force -Path $run | Out-Null

Copy-Item "$src\*" $run -Recurse -Force
"shot preview" | Set-Content (Join-Path $run "portable.txt") -Encoding utf8
# Skip the first-run wizard and the update check so the screenshot shows the UI.
$cfg = @{
    rootDirectory        = $run
    firstRunCompleted    = $true
    changelogSeenRound   = 999
    updateMode           = 3
    theme                = $Theme
    minimizeToTrayEnabled = $false
} | ConvertTo-Json -Compress
Set-Content (Join-Path $run "config.json") -Value $cfg -Encoding utf8

$env:AURORA_GLASS = $Glass
# Only one launcher may run (single-instance mutex): a leftover process would
# swallow the activation and we would screenshot the OLD window.
Get-Process -Name AuroraLauncher -ErrorAction SilentlyContinue | ForEach-Object {
    try { $_.Kill(); $_.WaitForExit(3000) } catch { }
}
Start-Sleep -Milliseconds 600
$proc = Start-Process -FilePath (Join-Path $run "AuroraLauncher.exe") -PassThru -WindowStyle Normal
Start-Sleep -Seconds 6

Add-Type -AssemblyName System.Drawing
Add-Type @"
using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
public class ShotWin32 {
    [StructLayout(LayoutKind.Sequential)] public struct RECT { public int Left, Top, Right, Bottom; }
    public delegate bool EnumProc(IntPtr hWnd, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hWnd, int cmd);
    [DllImport("user32.dll")] public static extern bool IsWindowVisible(IntPtr hWnd);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool EnumWindows(EnumProc cb, IntPtr p);
    public static IntPtr LargestWindow(uint processId) {
        IntPtr best = IntPtr.Zero; long bestArea = 0;
        EnumWindows((h, p) => {
            uint pid; GetWindowThreadProcessId(h, out pid);
            if (pid != processId || !IsWindowVisible(h)) return true;
            RECT r; if (!GetWindowRect(h, out r)) return true;
            long area = (long)(r.Right - r.Left) * (r.Bottom - r.Top);
            if (area > bestArea) { bestArea = area; best = h; }
            return true;
        }, IntPtr.Zero);
        return best;
    }
}
"@

$proc.Refresh()
$handle = $proc.MainWindowHandle
if ($handle -eq [IntPtr]::Zero) { $handle = [ShotWin32]::LargestWindow([uint32]$proc.Id) }
if ($handle -eq [IntPtr]::Zero) { throw "no visible window for pid $($proc.Id)" }
# SW_RESTORE (9), not SW_SHOW: a window started minimised reports a 353x56 rect
# and the capture would be a strip of the taskbar instead of the UI.
[ShotWin32]::ShowWindow($handle, 9) | Out-Null
[ShotWin32]::SetForegroundWindow($handle) | Out-Null
Start-Sleep -Milliseconds 1200

$rect = New-Object ShotWin32+RECT
[ShotWin32]::GetWindowRect($handle, [ref]$rect) | Out-Null
$w = $rect.Right - $rect.Left
$h = $rect.Bottom - $rect.Top

# The window is DPI-scaled: capture the whole screen, then crop with the
# physical rect (GetWindowRect already returns physical pixels).
Add-Type -AssemblyName System.Windows.Forms
$screen = [System.Windows.Forms.Screen]::PrimaryScreen.Bounds
$full = New-Object System.Drawing.Bitmap($screen.Width, $screen.Height)
$fg = [System.Drawing.Graphics]::FromImage($full)
$fg.CopyFromScreen($screen.X, $screen.Y, 0, 0, $full.Size)

$crop = New-Object System.Drawing.Rectangle(
    [Math]::Max(0, $rect.Left - $screen.X), [Math]::Max(0, $rect.Top - $screen.Y),
    [Math]::Min($w, $screen.Width), [Math]::Min($h, $screen.Height))
$bmp = $full.Clone($crop, $full.PixelFormat)
if ($MaxWidth -gt 0 -and $bmp.Width -gt $MaxWidth) {
    $ratio = $MaxWidth / $bmp.Width
    $small = New-Object System.Drawing.Bitmap($MaxWidth, [int]($bmp.Height * $ratio))
    $sg = [System.Drawing.Graphics]::FromImage($small)
    $sg.InterpolationMode = [System.Drawing.Drawing2D.InterpolationMode]::HighQualityBicubic
    $sg.DrawImage($bmp, 0, 0, $small.Width, $small.Height)
    $sg.Dispose()
    $bmp.Dispose()
    $bmp = $small
}
$bmp.Save($Out, [System.Drawing.Imaging.ImageFormat]::Png)
$fg.Dispose(); $full.Dispose(); $bmp.Dispose()

try { $proc.Kill() } catch { }
Start-Sleep -Milliseconds 500
try { Remove-Item $run -Recurse -Force } catch { }
Write-Output "SAVED $Out ($w x $h)"
