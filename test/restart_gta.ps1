# restart_gta.ps1 [-Marker text]  - closes GTA cleanly and starts it again exactly once.
# Never force-closes the Rockstar launcher: overlapping launches make it try to reinstall its
# prerequisites, which pops up an administrator prompt.
param([string]$Marker = "restart", [string]$Game = 'A:\GTAV', [int]$TimeoutSec = 300, [switch]$Install)
$epic = 'com.epicgames.launcher://apps/0584d2013f0149a791e7b9bad0eec102%3A6e563a2c0f5f46e3b4e88b5f4ed50cca%3A9d2d0eb64d5c44529cece33fe2a46482?action=launch&silent=true'
$log = Join-Path $Game 'CoopPlus\CoopPlus.log'
$g = Get-Process GTA5 -ErrorAction SilentlyContinue
if ($g) { $g.CloseMainWindow() | Out-Null; if (-not $g.WaitForExit(20000)) { Stop-Process -Id $g.Id -Force; Start-Sleep 2 } }
# let the Rockstar launcher close by itself
$t0 = Get-Date
while ((Get-Process PlayGTAV, Launcher -ErrorAction SilentlyContinue) -and ((Get-Date) - $t0).TotalSeconds -lt 45) { Start-Sleep 2 }
if (Get-Process PlayGTAV, Launcher -ErrorAction SilentlyContinue) { 'Rockstar launcher still open after 45 s - not launching (would trigger an admin prompt)'; exit 1 }
if ($Install) {
    $src = Join-Path (Split-Path -Parent (Split-Path -Parent $MyInvocation.MyCommand.Path)) 'src'
    Copy-Item (Join-Path $src 'out\TwoScreens.asi') (Join-Path $Game 'TwoScreens.asi') -Force
    & (Join-Path $src 'build_cs.ps1') -Install -Loader | Out-Null
    'installed add-on + scripts'
}
Start-Sleep 3
Add-Content $log "==== $Marker"
Start-Process $epic
if (-not ('StoryClick' -as [type])) {
Add-Type @'
using System; using System.Runtime.InteropServices;
public static class StoryClick {
  [DllImport("user32.dll")] static extern bool SetCursorPos(int x, int y);
  [DllImport("user32.dll")] static extern void mouse_event(uint f, uint dx, uint dy, uint d, UIntPtr e);
  [DllImport("user32.dll")] static extern void keybd_event(byte vk, byte scan, uint flags, UIntPtr extra);
  [DllImport("user32.dll")] static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
  // clicks "STORY MODE" on the start screen (bottom-right tab at 1920x1080)
  public static void Click(IntPtr gta) {
    SetThreadDpiAwarenessContext((IntPtr)(-4));
    keybd_event(0x12, 0, 0, UIntPtr.Zero); keybd_event(0x12, 0, 2, UIntPtr.Zero); SetForegroundWindow(gta);
    System.Threading.Thread.Sleep(500);
    SetCursorPos(1760, 1020); System.Threading.Thread.Sleep(200); SetCursorPos(1780, 1026); System.Threading.Thread.Sleep(200);
    mouse_event(2, 0, 0, 0, UIntPtr.Zero); System.Threading.Thread.Sleep(100); mouse_event(4, 0, 0, 0, UIntPtr.Zero);
  }
}
'@ }
$shv = Join-Path $Game 'ScriptHookV.log'
$t0 = Get-Date; $lastClick = Get-Date '2000-01-01'
while (((Get-Date) - $t0).TotalSeconds -lt $TimeoutSec) {
    Start-Sleep 3
    # still on the start screen (window up, scripts not started yet): click Story Mode every 8 s
    $gp = Get-Process GTA5 -ErrorAction SilentlyContinue
    $onStart = $gp -and $gp.MainWindowHandle -ne 0 -and ((Get-Date) - $gp.StartTime).TotalSeconds -gt 35
    if ($onStart -and ((Get-Date) - $lastClick).TotalSeconds -gt 10) {
        $l0 = Get-Content $log; $m0 = ($l0 | Select-String ([regex]::Escape("==== $Marker")) | Select-Object -Last 1).LineNumber
        if (-not (($l0 | Select-Object -Skip $m0) -match 'core (re)?loaded|controllers read')) { [StoryClick]::Click($gp.MainWindowHandle); $lastClick = Get-Date; 'clicked Story Mode' }
    }
    $err = Get-Process PlayGTAV -ErrorAction SilentlyContinue | Where-Object MainWindowTitle -eq 'Error'
    if ($err) { 'launch failed (Rockstar error window)'; exit 2 }
    $l = Get-Content $log; $m = ($l | Select-String ([regex]::Escape("==== $Marker")) | Select-Object -Last 1).LineNumber
    $after = $l | Select-Object -Skip $m
    if ($after -match 'core (re)?loaded|controllers read') { 'game ready'; $after | Select-Object -First 3; exit 0 }
}
'timed out waiting for Story Mode'; exit 3
