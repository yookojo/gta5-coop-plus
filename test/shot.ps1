# shot.ps1 <name> [mon|tv|both]  - screenshot the main monitor and/or TV into the scratch folder (half size)
param([string]$Name = 'shot', [string]$Which = 'both', [string]$Out = "$env:TEMP\coopplus-shots")
Add-Type -AssemblyName System.Drawing
if (-not ('ShotDpi' -as [type])) {
Add-Type @'
using System; using System.Runtime.InteropServices; public static class ShotDpi { [DllImport("user32.dll")] public static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c); }
'@ }
[ShotDpi]::SetThreadDpiAwarenessContext([IntPtr]-4) | Out-Null
New-Item -ItemType Directory -Force $Out | Out-Null
$canvas = New-Object Drawing.Bitmap 1920, 540
$cg = [Drawing.Graphics]::FromImage($canvas)
function Grab($x, $y, $w, $h, $dx) {
    $b = New-Object Drawing.Bitmap $w, $h; $g = [Drawing.Graphics]::FromImage($b); $g.CopyFromScreen($x, $y, 0, 0, $b.Size)
    $cg.DrawImage($b, $dx, 0, 960, 540); $g.Dispose(); $b.Dispose()
}
if ($Which -ne 'tv')  { Grab 0 0 1920 1080 0 }
if ($Which -ne 'mon') { Grab -5760 0 3840 2160 960 }
$p = Join-Path $Out "$Name.png"; $canvas.Save($p); $cg.Dispose(); $canvas.Dispose(); $p
