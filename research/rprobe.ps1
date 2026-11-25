# rprobe.ps1 - READ-ONLY probe of GTA V's renderer structures in the running game (nothing is written).
# Checks FiveM's byte patterns against this game build and reads the data behind them.
param([switch]$DumpSkeleton)
$ErrorActionPreference = 'Stop'
if (-not ('RProbe' -as [type])) {
Add-Type @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Diagnostics;
public static class RProbe {
  [DllImport("kernel32.dll")] static extern IntPtr OpenProcess(uint a, bool i, int pid);
  [DllImport("kernel32.dll")] static extern bool ReadProcessMemory(IntPtr h, IntPtr addr, byte[] buf, IntPtr size, out IntPtr read);
  static IntPtr h; public static long Base; public static byte[] Image;
  public static void Open() {
    var p = Process.GetProcessesByName("GTA5")[0];
    h = OpenProcess(0x0400 | 0x0010, false, p.Id);
    Base = p.MainModule.BaseAddress.ToInt64(); int size = p.MainModule.ModuleMemorySize;
    Image = new byte[size];
    for (int off = 0; off < size; off += 0x10000) { int n = Math.Min(0x10000, size - off); var b = new byte[n]; IntPtr rd; if (ReadProcessMemory(h, (IntPtr)(Base + off), b, (IntPtr)n, out rd)) Buffer.BlockCopy(b, 0, Image, off, (int)rd); }
  }
  public static byte[] Read(long addr, int n) { var b = new byte[n]; IntPtr rd; ReadProcessMemory(h, (IntPtr)addr, b, (IntPtr)n, out rd); return b; }
  public static long ReadPtr(long addr) { return BitConverter.ToInt64(Read(addr, 8), 0); }
  // pattern like "48 8B 3D ? ? ? ?" -> all match offsets (relative to the module)
  public static List<int> Find(string pat, int max) {
    var parts = pat.Split(' '); var bytes = new int[parts.Length];
    for (int i = 0; i < parts.Length; i++) bytes[i] = parts[i] == "?" ? -1 : Convert.ToInt32(parts[i], 16);
    var res = new List<int>(); byte first = (byte)bytes[0];
    for (int i = 0; i < Image.Length - bytes.Length; i++) {
      if (Image[i] != first) continue;
      bool ok = true; for (int j = 1; j < bytes.Length; j++) if (bytes[j] >= 0 && Image[i + j] != bytes[j]) { ok = false; break; }
      if (ok) { res.Add(i); if (res.Count >= max) break; }
    }
    return res;
  }
  public static long Rip(int off) { return Base + off + 4 + BitConverter.ToInt32(Image, off); }   // RIP-relative operand at module offset
  public static uint Joaat(string s) { uint h = 0; foreach (char c in s.ToLowerInvariant()) { h += c; h += h << 10; h ^= h >> 6; } h += h << 3; h ^= h >> 11; h += h << 15; return h; }
  public static float[] Floats(long addr, int n) { var b = Read(addr, n * 4); var f = new float[n]; for (int i = 0; i < n; i++) f[i] = BitConverter.ToSingle(b, i * 4); return f; }
}
'@ }
[RProbe]::Open()
"GTA5.exe at 0x{0:X}, image {1:N0} bytes read" -f [RProbe]::Base, [RProbe]::Image.Length

function Probe($name, $pattern, $ripOff) {
    $m = [RProbe]::Find($pattern, 4)
    if ($m.Count -eq 0) { Write-Host "  [no match]  $name"; return $null }
    $target = [RProbe]::Rip($m[0] + $ripOff)
    Write-Host ("  [found x$($m.Count)] $name -> pattern at +0x{0:X}, points to 0x{1:X}" -f $m[0], $target)
    return $target
}
"--- FiveM patterns on this build"
$cur = Probe 'spdViewport::sm_Current (viewport being drawn)' '48 8B 3D ? ? ? ? 40 8A F2 48 8B D9 75 14' 3
$vg  = Probe 'g_viewportGame (the game viewport)'            '33 C0 48 39 05 ? ? ? ? 74 2E 48 8B 0D ? ? ? ? 48 85 C9 74 22' 5
if (-not $vg) { $vg = Probe 'g_viewportGame (alt pattern)' '0F 2F F0 76 ? 4C 8B 35' 8 }
$sk  = Probe 'rage::gameSkeleton instance'                    '48 8D 0D ? ? ? ? BA 02 00 00 00 84 DB 75 05' 3

if ($vg) {
    $vp = [RProbe]::ReadPtr($vg)
    $inv = [RProbe]::Floats($vp + 0x10 + 3 * 64, 16)       # grcViewport.m_inverseView: camera -> world
    "--- CViewportGame at 0x{0:X}: camera position from m_inverseView = {1:F1}, {2:F1}, {3:F1}" -f $vp, $inv[12], $inv[13], $inv[14]
    $l = Get-Content 'A:\GTAV\CoopPlus\CoopPlus.log' -Tail 60 | Select-String 'rendering ([-\d\.]+),([-\d\.]+),([-\d\.]+)' | Select-Object -Last 1
    if ($l) { "    game's own camera (GET_FINAL_RENDERED_CAM_COORD, from CoopPlus log): $($l.Matches[0].Groups[1].Value), $($l.Matches[0].Groups[2].Value), $($l.Matches[0].Groups[3].Value)" }
}
if ($sk) {
    # gameSkeleton.m_initFunctions: atArray<InitFunctionData> at +24 { ptr, u16 count, u16 size }; entry = 32 bytes
    $arr = [RProbe]::ReadPtr($sk + 24); $cnt = [BitConverter]::ToUInt16([RProbe]::Read($sk + 32, 2), 0)
    "--- game skeleton: $cnt init functions"
    $want = @{}; foreach ($n in 'CreateFinalScreenRenderPhaseList', 'ViewportSystemInit', 'ViewportSystemInitLevel', 'CRenderPhaseCascadeShadowsInterface', 'Viewport') { $want[[RProbe]::Joaat($n)] = $n }
    for ($i = 0; $i -lt $cnt; $i++) {
        $e = [RProbe]::Read($arr + $i * 32, 32)
        $init = [BitConverter]::ToInt64($e, 0); $hash = [BitConverter]::ToUInt32($e, 24)
        if ($want.ContainsKey($hash)) { "  {0,-40} init fn 0x{1:X} (module +0x{2:X})" -f $want[$hash], $init, ($init - [RProbe]::Base) }
        elseif ($DumpSkeleton) { "  #{0} hash {1:X8} init 0x{2:X}" -f $i, $hash, $init }
    }
}
