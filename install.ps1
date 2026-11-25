# CoopPlus installer - local two-player co-op for GTA V Legacy (story mode).
#
#   powershell -ExecutionPolicy Bypass -File install.ps1            install / update
#   powershell -ExecutionPolicy Bypass -File install.ps1 -Uninstall remove everything it added
#   -GamePath "D:\Games\GTAV"   if it can't find the game
#   -NoAdminPromptFix           don't replace PlayGTAV.exe (Epic) - see README
param([string]$GamePath, [switch]$Uninstall, [switch]$NoAdminPromptFix)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$files = Join-Path $here 'files'
function Say($m, $c = 'Gray') { Write-Host $m -ForegroundColor $c }

# ---------------------------------------------------------------- find the game
function Find-Gta {
    $c = @()
    $epic = 'C:\ProgramData\Epic\EpicGamesLauncher\Data\Manifests'
    if (Test-Path $epic) { Get-ChildItem $epic -Filter *.item | ForEach-Object { try { $j = Get-Content $_.FullName -Raw | ConvertFrom-Json; if ($j.DisplayName -match '^Grand Theft Auto V$') { $c += $j.InstallLocation } } catch {} } }
    foreach ($k in 'HKLM:\SOFTWARE\WOW6432Node\Rockstar Games\Grand Theft Auto V', 'HKLM:\SOFTWARE\WOW6432Node\Rockstar Games\GTAV') {
        try { $p = (Get-ItemProperty $k -ErrorAction Stop).InstallFolder; if ($p) { $c += $p } } catch {} }
    try {
        $steam = (Get-ItemProperty 'HKCU:\Software\Valve\Steam' -ErrorAction Stop).SteamPath
        $vdf = Join-Path $steam 'steamapps\libraryfolders.vdf'
        if (Test-Path $vdf) { [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"') | ForEach-Object { $c += (Join-Path ($_.Groups[1].Value -replace '\\\\', '\') 'steamapps\common\Grand Theft Auto V') } }
    } catch {}
    foreach ($p in $c) { if ($p -and (Test-Path (Join-Path $p 'GTA5.exe'))) { return $p } }
    return $null
}
if (-not $GamePath) { $GamePath = Find-Gta }
if (-not $GamePath -or -not (Test-Path (Join-Path $GamePath 'GTA5.exe'))) { Say "Couldn't find GTA V. Run again with -GamePath ""<folder with GTA5.exe>""." Red; exit 1 }
if (Test-Path (Join-Path $GamePath 'GTA5_Enhanced.exe')) { Say "This is GTA V Enhanced. CoopPlus only supports GTA V Legacy (DirectX 11)." Red; exit 1 }
Say "GTA V: $GamePath" Cyan
if (Get-Process GTA5 -ErrorAction SilentlyContinue) { Say "Close GTA V first, then run this again." Red; exit 1 }

$coop = Join-Path $GamePath 'CoopPlus'; $disabled = Join-Path $GamePath '_disabled_by_coopplus'
$settings = @("$env:USERPROFILE\Documents\Rockstar Games\GTA V\settings.xml", "$env:USERPROFILE\OneDrive\Documents\Rockstar Games\GTA V\settings.xml") | Where-Object { Test-Path $_ } | Select-Object -First 1

# ---------------------------------------------------------------- uninstall
if ($Uninstall) {
    foreach ($f in 'TwoScreens.asi', 'scripts\CoopPlus.dll') { $p = Join-Path $GamePath $f; if (Test-Path $p) { Remove-Item $p -Force; Say "removed $f" } }
    $orig = Join-Path $GamePath 'PlayGTAV.original.exe'
    if (Test-Path $orig) { Move-Item $orig (Join-Path $GamePath 'PlayGTAV.exe') -Force; Say 'restored the original PlayGTAV.exe' }
    if (Test-Path $disabled) { Get-ChildItem $disabled -Recurse -File | ForEach-Object { $rel = $_.FullName.Substring($disabled.Length + 1); $dst = Join-Path $GamePath $rel; New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null; Move-Item $_.FullName $dst -Force; Say "restored $rel" }; Remove-Item $disabled -Recurse -Force }
    Say "CoopPlus removed. Your settings and Player 2's money/loadout stay in $coop (delete it if you like)." Green
    exit 0
}

# ---------------------------------------------------------------- requirements
$gameVer = (Get-Item (Join-Path $GamePath 'GTA5.exe')).VersionInfo.FileVersion
$build = ($gameVer -split '\.')[2]
Say "Game version $gameVer"
$shv = Join-Path $GamePath 'ScriptHookV.dll'
if (-not (Test-Path $shv)) {
    Say "Script Hook V is missing. Download it from http://www.dev-c.com/gtav/scripthookv/ (version $build) and copy everything in its 'bin' folder next to GTA5.exe, then run this again." Red; exit 1 }
$shvBuild = ((Get-Item $shv).VersionInfo.FileVersion -split '\.')[0]
if ($shvBuild -ne $build) { Say "Script Hook V ($((Get-Item $shv).VersionInfo.FileVersion)) doesn't match the game ($build). Get the matching version from dev-c.com first." Yellow }
if (-not (Test-Path (Join-Path $GamePath 'ScriptHookVDotNet3.dll'))) {
    Say 'Installing Script Hook V .NET (nightly, needed for current game builds)...'
    $rel = Invoke-RestMethod 'https://api.github.com/repos/scripthookvdotnet/scripthookvdotnet-nightly/releases/latest' -UseBasicParsing
    $zipUrl = ($rel.assets | Where-Object name -like '*.zip' | Select-Object -First 1).browser_download_url
    $tmp = Join-Path $env:TEMP 'shvdn-coopplus'; Remove-Item $tmp -Recurse -Force -ErrorAction SilentlyContinue; New-Item -ItemType Directory $tmp | Out-Null
    Invoke-WebRequest $zipUrl -OutFile "$tmp\shvdn.zip" -UseBasicParsing; Expand-Archive "$tmp\shvdn.zip" $tmp -Force
    foreach ($f in 'ScriptHookVDotNet-Loader.asi', 'ScriptHookVDotNet.dll', 'ScriptHookVDotNet2.dll', 'ScriptHookVDotNet3.dll', 'ScriptHookVDotNet.ini') { Copy-Item (Join-Path $tmp $f) $GamePath -Force }
    Say "Script Hook V .NET $($rel.tag_name) installed" Green
}

# ---------------------------------------------------------------- set aside mods that conflict
New-Item -ItemType Directory -Force $disabled | Out-Null
foreach ($f in 'OfflineCoopMod.asi', 'XInputBlock.asi', 'scripts\OfflineCoop.dll') {
    $p = Join-Path $GamePath $f
    if (Test-Path $p) { $dst = Join-Path $disabled $f; New-Item -ItemType Directory -Force (Split-Path $dst) | Out-Null; Move-Item $p $dst -Force; Say "set aside $f (it does the same job)" Yellow }
}

# ---------------------------------------------------------------- copy CoopPlus
New-Item -ItemType Directory -Force (Join-Path $GamePath 'scripts'), $coop, (Join-Path $coop 'core') | Out-Null
Copy-Item (Join-Path $files 'TwoScreens.asi') $GamePath -Force
Copy-Item (Join-Path $files 'CoopPlus.dll') (Join-Path $GamePath 'scripts') -Force
Copy-Item (Join-Path $files 'CoopPlusCore.dll') (Join-Path $coop 'core') -Force
if (-not (Test-Path (Join-Path $coop 'CoopPlus.ini'))) { Copy-Item (Join-Path $files 'CoopPlus.ini') $coop }
Copy-Item "$env:WINDIR\System32\xinput1_4.dll" (Join-Path $coop 'xinput_p2.dll') -Force
Say 'CoopPlus files copied' Green

# ---------------------------------------------------------------- game settings: borderless window, keep running when unfocused
if ($settings) {
    Copy-Item $settings "$settings.before-coopplus" -Force -ErrorAction SilentlyContinue
    $x = [IO.File]::ReadAllText($settings)
    $x = $x -replace '<Windowed value="\d+" />', '<Windowed value="2" />' -replace '<PauseOnFocusLoss value="\d" />', '<PauseOnFocusLoss value="0" />'
    [IO.File]::WriteAllText($settings, $x)
    Say 'GTA set to borderless window and to keep running when another window is clicked' Green
} else { Say 'GTA settings not found yet - start the game once, then run this again for the best setup.' Yellow }

# ---------------------------------------------------------------- Epic: stop the admin prompt on every launch
$stub = Join-Path $GamePath 'PlayGTAV.exe'
$isEpic = Test-Path (Join-Path $GamePath '.egstore')
if ($isEpic -and -not $NoAdminPromptFix -and (Test-Path $stub)) {
    $orig = Join-Path $GamePath 'PlayGTAV.original.exe'
    if (-not (Test-Path $orig)) { Move-Item $stub $orig -Force }
    Copy-Item (Join-Path $files 'PlayGTAV.exe') $stub -Force
    Say 'Epic launch fixed: no more admin prompt every time (original kept as PlayGTAV.original.exe)' Green
}

Say ''
Say 'Done. Start GTA V Story Mode, then:' Cyan
Say '  N            add / remove Player 2 (or Back + Start on a controller, Share + Options on PlayStation)'
Say '  F7           co-op menu (screens, friendly fire, activities...)    Player 2: Back button'
Say '  K / F9       split screen on/off / swap screens'
