# run.ps1 <test file(s)>  - runs scripted in-game tests through CoopPlus' command file and checks the log.
#
# Test file format (one per line):
#   any CoopPlus command (spawn 0, pad ..., wait 500, ...)   -> sent to the game in order
#   #expect <regex>          -> must appear in the log while the test runs
#   #reject <regex>          -> must NOT appear (default: ERROR|cmd failed)
#   #rec <tv|mon> <name> <frames> <fps> [delaySec]  -> record a screen (contact sheet) after delaySec
#   #timeout <sec>           -> max time to wait for the test to finish (default 30)
param([Parameter(Mandatory)][string[]]$Tests, [string]$Game = 'A:\GTAV')
$log = Join-Path $Game 'CoopPlus\CoopPlus.log'; $cmd = Join-Path $Game 'CoopPlus\cmd.txt'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$allOk = $true
foreach ($t in $Tests) {
    $path = if (Test-Path $t) { $t } else { Join-Path $here "cases\$t.txt" }
    $name = [IO.Path]::GetFileNameWithoutExtension($path)
    $id = "$name-" + (Get-Random -Maximum 99999)
    $lines = Get-Content $path
    $send = @("mark BEGIN $id"); $expect = @(); $reject = @('ERROR', 'cmd failed'); $recs = @(); $timeout = 30
    foreach ($l in $lines) {
        $s = $l.Trim(); if (-not $s -or $s.StartsWith('//')) { continue }
        if ($s -match '^#expect\s+(.+)$') { $expect += $Matches[1] }
        elseif ($s -match '^#reject\s+(.+)$') { $reject += $Matches[1] }
        elseif ($s -match '^#rec\s+(\S+)\s+(\S+)\s+(\d+)\s+(\d+)\s*(\S*)') { $recs += [pscustomobject]@{ Screen = $Matches[1]; Name = $Matches[2]; Frames = [int]$Matches[3]; Fps = [int]$Matches[4]; At = $(if ($Matches[5]) { [double]$Matches[5] } else { 0 }) } }
        elseif ($s -match '^#timeout\s+(\d+)') { $timeout = [int]$Matches[1] }
        else { $send += $s }
    }
    $send += "mark END $id"
    $startLine = (Get-Content $log).Count
    Set-Content $cmd ($send -join "`r`n") -Encoding ascii
    $t0 = Get-Date
    foreach ($r in @($recs | Sort-Object At)) {
        $wait = $r.At - ((Get-Date) - $t0).TotalSeconds; if ($wait -gt 0) { Start-Sleep -Milliseconds ([int]($wait * 1000)) }
        & (Join-Path $here 'rec.ps1') $r.Name $r.Screen $r.Frames $r.Fps | Out-Null
    }
    $done = $false
    while (((Get-Date) - $t0).TotalSeconds -lt $timeout) {
        $new = Get-Content $log | Select-Object -Skip $startLine
        if ($new -match "MARK END $id") { $done = $true; break }
        Start-Sleep -Milliseconds 400
    }
    $new = Get-Content $log | Select-Object -Skip $startLine
    $fails = @()
    if (-not $done) { $fails += "did not finish within $timeout s (is the game in Story Mode?)" }
    foreach ($e in $expect) { if (-not ($new -match $e)) { $fails += "missing: $e" } }
    foreach ($r in $reject) { $hit = $new | Where-Object { $_ -match $r } | Select-Object -First 1; if ($hit) { $fails += "unwanted: $hit" } }
    if ($fails) { $allOk = $false; "FAIL $name"; $fails | ForEach-Object { "   - $_" } } else { "PASS $name" }
    foreach ($r in $recs) { "   rec: $env:TEMP\coopplus-shots\$($r.Name).png" }
}
if (-not $allOk) { exit 1 }
