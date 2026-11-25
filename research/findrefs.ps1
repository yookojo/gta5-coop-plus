# findrefs.ps1 - READ-ONLY: find heap objects that point at GTA's viewports (render phases hold one).
param([int]$Back = 0x3000, [switch]$Each)
. (Join-Path (Split-Path -Parent $MyInvocation.MyCommand.Path) 'rprobe.ps1') | Out-Null
if (-not ('HeapRefs' -as [type])) {
Add-Type @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Diagnostics;
public static class HeapRefs {
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint a, bool i, int pid);
  [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
  [DllImport("kernel32.dll")] static extern int VirtualQueryEx(IntPtr h, IntPtr addr, out MBI m, uint len);
  [StructLayout(LayoutKind.Sequential)] public struct MBI { public IntPtr Base, AllocBase; public uint AllocProtect; public uint pad1; public IntPtr Size; public uint State, Protect, Type, pad2; }
  // every 8-byte-aligned heap address holding one of the given values
  public static List<long[]> Find(long[] values) {
    var set = new HashSet<long>(values); var res = new List<long[]>();
    var p = Process.GetProcessesByName("GTA5")[0]; IntPtr h = OpenProcess(0x0400 | 0x0010, false, p.Id);
    long addr = 0; MBI m; var buf = new byte[4 << 20];
    while (VirtualQueryEx(h, (IntPtr)addr, out m, (uint)Marshal.SizeOf(typeof(MBI))) != 0) {
      long size = (long)m.Size;
      bool ok = m.State == 0x1000 && m.Type == 0x20000 && (m.Protect & 0x04) != 0 && (m.Protect & 0x100) == 0;   // committed, private, read/write
      if (ok) for (long off = 0; off < size; off += buf.Length) {
        int n = (int)Math.Min(buf.Length, size - off); IntPtr rd;
        if (!ReadProcessMemory(h, (IntPtr)(addr + off), buf, (IntPtr)n, out rd)) continue;
        for (int i = 0; i + 8 <= (int)rd; i += 8) { long v = BitConverter.ToInt64(buf, i); if (set.Contains(v)) res.Add(new long[] { addr + off + i, v }); }
      }
      addr += Math.Max(size, 4096); if (addr <= 0 || addr > 0x7FFFFFFFFFFF) break;
    }
    return res;
  }
}
'@ }
$base = [RProbe]::Base; $size = [RProbe]::Image.Length
function InModule($v) { return $v -ge $base -and $v -lt ($base + $size) }
# the viewport manager list at +0x1F6B300 (atArray: ptr, count) and the game viewport at +0x1F6B320
$listPtr = [RProbe]::ReadPtr($base + 0x1F6B300); $listCnt = [BitConverter]::ToUInt16([RProbe]::Read($base + 0x1F6B308, 2), 0)
$vps = @(); for ($i = 0; $i -lt [Math]::Min($listCnt, 8); $i++) { $vps += [RProbe]::ReadPtr($listPtr + 8 * $i) }
$game = [RProbe]::ReadPtr($base + 0x1F6B320)
"viewport list: $listCnt entries"; foreach ($v in $vps) { "  viewport 0x{0:X}  class table +0x{1:X}{2}" -f $v, ([RProbe]::ReadPtr($v) - $base), $(if ($v -eq $game) { '   <- game viewport' } else { '' }) }
$hits = [HeapRefs]::Find([long[]]$vps)
"objects pointing at a viewport: $($hits.Count)"
$groups = @{}
foreach ($hit in $hits) {
    $at = $hit[0]; $v = $hit[1]; $obj = $null; $vt = 0; $fieldOff = 0
    # walk back to the nearest qword that is a class table pointer (points into the game image, whose first entry is code)
    for ($k = 0; $k -le $Back; $k += 8) {
        $q = [RProbe]::ReadPtr($at - $k)
        if ((InModule $q) -and (InModule ([RProbe]::ReadPtr($q)))) { $obj = $at - $k; $vt = $q - $base; $fieldOff = $k; break }
    }
    if ($Each) { Write-Host ("  {0} ref at 0x{1:X}: object 0x{2:X} class +0x{3:X} field +0x{4:X}" -f $(if ($v -eq $game) {"G"} else {"o"}), $at, $obj, $vt, $fieldOff) }
    $key = "{0:X}|{1:X}" -f $vt, $fieldOff
    if (-not $groups[$key]) { $groups[$key] = [System.Collections.ArrayList]@() }
    [void]$groups[$key].Add(@($obj, $v))
}
"grouped by class table (+offset of the viewport field):"
foreach ($k in ($groups.Keys | Sort-Object)) {
    $vt, $fo = $k.Split('|'); $e = $groups[$k]
    $which = ($e | ForEach-Object { if ($_[1] -eq $game) { 'game' } else { 'other' } } | Group-Object | ForEach-Object { "$($_.Count) $($_.Name)" }) -join ', '
    "  class +0x{0,-8} viewport field at +0x{1,-4} x{2,-3} ({3})  first obj 0x{4:X}" -f $vt, $fo, $e.Count, $which, $e[0][0]
}
