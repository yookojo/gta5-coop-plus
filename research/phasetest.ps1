# phasetest.ps1 - EXPERIMENT (writes to game memory, reversibly): does a render phase draw from its own viewport?
# 1) snapshot the game viewport while it holds Player 2's camera, into new memory inside the game
# 2) for each candidate phase: point its viewport field (+0x4C8) at the copy for ~1 s, screenshot, restore
param([int[]]$Phases = @(20, 18, 19, 17, 21, 13, 14, 15, 16), [int]$HoldMs = 1200, [string]$P2Yaw = "")
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'findrefs.ps1') 6>$null 2>$null | Out-Null
if (-not ('GameMem' -as [type])) {
Add-Type @'
using System; using System.Runtime.InteropServices; using System.Diagnostics;
public static class GameMem {
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint a, bool i, int pid);
  [DllImport("kernel32.dll")] static extern IntPtr VirtualAllocEx(IntPtr h, IntPtr addr, IntPtr size, uint type, uint protect);
  [DllImport("kernel32.dll")] static extern bool WriteProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr written);
  [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
  static IntPtr h;
  public static void Open() { h = OpenProcess(0x0008 | 0x0010 | 0x0020 | 0x0400, false, Process.GetProcessesByName("GTA5")[0].Id); }
  public static long Alloc(int n) { return VirtualAllocEx(h, IntPtr.Zero, (IntPtr)n, 0x3000, 0x04).ToInt64(); }
  public static bool Write(long addr, byte[] b) { IntPtr w; return WriteProcessMemory(h, (IntPtr)addr, b, (IntPtr)b.Length, out w); }
  public static byte[] Read(long addr, int n) { var b = new byte[n]; IntPtr r; ReadProcessMemory(h, (IntPtr)addr, b, (IntPtr)n, out r); return b; }
}
'@ }
[GameMem]::Open()
$base = [RProbe]::Base; $game = [RProbe]::ReadPtr($base + 0x1F6B320)
$cmd = 'A:\GTAV\CoopPlus\cmd.txt'; $log = 'A:\GTAV\CoopPlus\CoopPlus.log'
# the render phase list (28 entries) found by findrefs: locate it again by scanning for the first phase's address
$listPtr = [RProbe]::ReadPtr($base + 0x1F6B300); $vps = @(0..2 | ForEach-Object { [RProbe]::ReadPtr($listPtr + 8 * $_) })
$phaseObjs = @(); foreach ($hit in [HeapRefs]::Find([long[]]$vps)) { $o = $hit[0] - 0x4C8; $vt = [RProbe]::ReadPtr($o) - $base; if ($vt -ge 0x1A40000 -and $vt -lt 0x1A50000) { $phaseObjs += $o } }
$refs = [HeapRefs]::Find([long[]]$phaseObjs) | Sort-Object { $_[0] }
$best = @(); $run = @(); foreach ($r in $refs) { if ($run.Count -and $r[0] -eq $run[-1][0] + 8) { $run += , $r } else { if ($run.Count -gt $best.Count) { $best = $run }; $run = @(, $r) } }; if ($run.Count -gt $best.Count) { $best = $run }
$list = @($best | ForEach-Object { $_[1] }); "render phase list: $($list.Count) phases"

# 1) Player 2's camera: turn split on, find P2's camera position, wait until the game viewport holds it, copy it
$yaw = if ($P2Yaw) { "`r`ncamyaw $P2Yaw`r`nwait 800" } else { "" }
Set-Content $cmd "fresh`r`nsplit on$yaw`r`nstate" -Encoding ascii; Start-Sleep 5
$cl = (Get-Content $log -Tail 40 | Select-String 'cam: wanted ([-\d\.]+),([-\d\.]+),([-\d\.]+)' | Select-Object -Last 1).Matches[0].Groups
$p2 = @([double]$cl[1].Value, [double]$cl[2].Value, [double]$cl[3].Value); "Player 2 camera at $($p2 -join ', ')"
$snap = $null
for ($t = 0; $t -lt 400 -and -not $snap; $t++) {
    $b = [GameMem]::Read($game, 0x600)
    for ($o = 0x10; $o -lt 0x5C0; $o += 0x10) {   # any matrix row in the viewport = P2's camera position (w = 1)
        $x = [BitConverter]::ToSingle($b, $o); $y = [BitConverter]::ToSingle($b, $o + 4); $z = [BitConverter]::ToSingle($b, $o + 8); $w = [BitConverter]::ToSingle($b, $o + 12)
        if ([Math]::Abs($w - 1) -lt 0.001 -and [Math]::Abs($x - $p2[0]) -lt 0.2 -and [Math]::Abs($y - $p2[1]) -lt 0.2 -and [Math]::Abs($z - $p2[2]) -lt 0.2) { $snap = $b; break }
    }
}
if (-not $snap) { "could not catch the viewport holding Player 2's camera"; exit 1 }
$copy = [GameMem]::Alloc(0x1000); [void][GameMem]::Write($copy, $snap)
"copied the game viewport (holding Player 2's camera) to 0x{0:X} inside the game" -f $copy
Set-Content $cmd "split off" -Encoding ascii; Start-Sleep 2       # now the real viewport only shows Player 1

# 2) one phase at a time: draw from the copy, screenshot, restore
& (Join-Path (Split-Path $here) 'test\shot.ps1') 'pt_before' mon | Out-Null
foreach ($i in $Phases) {
    if (-not (Get-Process GTA5 -ErrorAction SilentlyContinue)) { "GTA closed (crash?) - stopping"; break }
    $ph = $list[$i]; $field = $ph + 0x4C8
    $orig = [GameMem]::Read($field, 8)
    [void][GameMem]::Write($field, [BitConverter]::GetBytes([long]$copy))
    Start-Sleep -Milliseconds $HoldMs
    & (Join-Path (Split-Path $here) 'test\shot.ps1') ("pt_phase$i") mon | Out-Null
    [void][GameMem]::Write($field, $orig)
    "phase [$i] class +0x{0:X}: done, restored" -f ([RProbe]::ReadPtr($ph) - $base)
    Start-Sleep -Milliseconds 600
}
"finished; game still running: $([bool](Get-Process GTA5 -ErrorAction SilentlyContinue))"
