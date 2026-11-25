# clonetest.ps1 - EXPERIMENT (reversible data writes): clone the main scene phase, bind the clone to Player 2's
# camera, swap it into one slot of the render phase list for a moment, screenshot, put everything back.
param([int]$Source = 4, [int]$Slot = 13, [int]$HoldMs = 1500, [string]$P2Yaw = "0", [int]$CopyBytes = 0x800)
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
. (Join-Path $here 'findrefs.ps1') 6>$null 2>$null | Out-Null
. (Join-Path $here 'phasetest.ps1') -Phases @() 6>$null 2>$null | Out-Null     # loads GameMem, finds the list, makes the Player 2 viewport copy ($copy)
if (-not $copy) { "no Player 2 viewport copy"; exit 1 }
$src = $list[$Source]; $slotAddr = $best[$Slot][0]; $origSlot = [GameMem]::Read($slotAddr, 8)
$clone = [GameMem]::Alloc(0x2000)
[void][GameMem]::Write($clone, [GameMem]::Read($src, $CopyBytes))
[void][GameMem]::Write($clone + 0x4C8, [BitConverter]::GetBytes([long]$copy))
"clone of phase [$Source] at 0x{0:X} bound to Player 2's camera; swapping it into slot [$Slot] (list entry 0x{1:X})" -f $clone, $slotAddr
& (Join-Path (Split-Path $here) 'test\shot.ps1') 'ct_before' mon | Out-Null
[void][GameMem]::Write($slotAddr, [BitConverter]::GetBytes([long]$clone))
Start-Sleep -Milliseconds $HoldMs
& (Join-Path (Split-Path $here) 'test\shot.ps1') ("ct_slot$Slot") mon | Out-Null
[void][GameMem]::Write($slotAddr, $origSlot)
"restored slot [$Slot]; game still running: $([bool](Get-Process GTA5 -ErrorAction SilentlyContinue))"
