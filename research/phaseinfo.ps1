# phaseinfo.ps1 - READ-ONLY: dump the render phase list header and the fields of chosen phases.
param([int[]]$Show = @(4, 17, 18, 19, 20, 21), [int]$Bytes = 0x520)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'findrefs.ps1') 6>$null 2>$null | Out-Null
$base = [RProbe]::Base; $size = [RProbe]::Image.Length
function InModule($v) { return $v -ge $base -and $v -lt ($base + $size) }
$listPtr = [RProbe]::ReadPtr($base + 0x1F6B300); $vps = @(0..2 | ForEach-Object { [RProbe]::ReadPtr($listPtr + 8 * $_) }); $game = $vps[1]
$phaseObjs = @(); foreach ($hit in [HeapRefs]::Find([long[]]$vps)) { $o = $hit[0] - 0x4C8; $vt = [RProbe]::ReadPtr($o) - $base; if ($vt -ge 0x1A40000 -and $vt -lt 0x1A50000) { $phaseObjs += $o } }
$refs = [HeapRefs]::Find([long[]]$phaseObjs) | Sort-Object { $_[0] }
$best = @(); $run = @(); foreach ($r in $refs) { if ($run.Count -and $r[0] -eq $run[-1][0] + 8) { $run += , $r } else { if ($run.Count -gt $best.Count) { $best = $run }; $run = @(, $r) } }; if ($run.Count -gt $best.Count) { $best = $run }
$data = $best[0][0]; $list = @($best | ForEach-Object { $_[1] })
"phase list data at 0x{0:X} ({1} entries)" -f $data, $list.Count
# who points at the list's data? (the atArray header: { T* data; u16 count; u16 capacity })
foreach ($h in [HeapRefs]::Find([long[]]@($data))) { $hb = [RProbe]::Read($h[0], 16); "  header at 0x{0:X}: count {1}, capacity {2}" -f $h[0], [BitConverter]::ToUInt16($hb, 8), [BitConverter]::ToUInt16($hb, 10) }
foreach ($i in $Show) {
    $o = $list[$i]; $b = [RProbe]::Read($o, $Bytes)
    "---- phase [$i] at 0x{0:X}, class +0x{1:X}" -f $o, ([RProbe]::ReadPtr($o) - $base)
    $vt = [RProbe]::ReadPtr($o); $fns = @(0..15 | ForEach-Object { "+0x{0:X}" -f ([RProbe]::ReadPtr($vt + 8 * $_) - $base) }); "  class table functions: $($fns -join ' ')"
    $line = @()
    for ($k = 8; $k -lt $Bytes; $k += 8) {
        $q = [BitConverter]::ToInt64($b, $k); $lo = [BitConverter]::ToInt32($b, $k); $hi = [BitConverter]::ToInt32($b, $k + 4)
        $desc = if ($q -eq 0) { $null } elseif (InModule $q) { "img+0x{0:X}" -f ($q - $base) } elseif ($q -gt 0x10000000000 -and $q -lt 0x7FF000000000) { if ($vps -contains $q) { "VIEWPORT" } elseif ($list -contains $q) { "PHASE[" + [array]::IndexOf($list, $q) + "]" } else { "heap 0x{0:X}" -f $q } } else { "{0} {1}  ({2:F3} {3:F3})" -f $lo, $hi, [BitConverter]::ToSingle($b, $k), [BitConverter]::ToSingle($b, $k + 4) }
        if ($desc) { $line += ("+0x{0:X3} {1}" -f $k, $desc) }
    }
    $line | Select-Object -First 60
}
