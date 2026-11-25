# windows.ps1 [-Move <class> -X <x> -Y <y>]  - list GTA's visible windows (class, position); optionally move one.
# (Process.MainWindowHandle is NOT the game window when Player 2's window is in front - always look up by class.)
param([string]$Move, [int]$X, [int]$Y)
if (-not ('GtaWin' -as [type])) {
Add-Type @'
using System; using System.Collections.Generic; using System.Runtime.InteropServices; using System.Text;
public static class GtaWin {
  public delegate bool EP(IntPtr h, IntPtr p);
  [DllImport("user32.dll")] static extern bool EnumWindows(EP f, IntPtr p);
  [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr h, out uint pid);
  [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr h, StringBuilder s, int n);
  [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr h);
  [StructLayout(LayoutKind.Sequential)] public struct R { public int L, T, Ri, B; }
  [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out R r);
  [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr h, IntPtr after, int x, int y, int w, int hh, uint f);
  [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr c);
  public static Dictionary<string, IntPtr> Find(uint pid) {
    SetThreadDpiAwarenessContext((IntPtr)(-4));
    var d = new Dictionary<string, IntPtr>();
    EnumWindows((h, p) => { uint q; GetWindowThreadProcessId(h, out q); if (q == pid && IsWindowVisible(h)) { var sb = new StringBuilder(128); GetClassNameW(h, sb, 128); d[sb.ToString()] = h; } return true; }, IntPtr.Zero);
    return d;
  }
  public static R Rect(IntPtr h) { R r; GetWindowRect(h, out r); return r; }
  public static void MoveTo(IntPtr h, int x, int y) { R r = Rect(h); SetWindowPos(h, IntPtr.Zero, x, y, r.Ri - r.L, r.B - r.T, 0x14); }
}
'@ }
$w = [GtaWin]::Find((Get-Process GTA5).Id)
if ($Move) { if ($w.ContainsKey($Move)) { [GtaWin]::MoveTo($w[$Move], $X, $Y) } else { "no visible window of class $Move" } }
foreach ($k in $w.Keys) { $r = [GtaWin]::Rect($w[$k]); "{0,-18} {1},{2} {3}x{4}" -f $k, $r.L, $r.T, ($r.Ri - $r.L), ($r.B - $r.T) }
