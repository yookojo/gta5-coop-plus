# rec.ps1 <name> [screen: mon|tv] [frames] [fps]
# Records consecutive frames of one screen with Desktop Duplication (sees game + TV window correctly)
# and tiles them into one contact sheet, read left-to-right, top-to-bottom.
param([string]$Name = 'rec', [string]$Screen = 'tv', [int]$Frames = 24, [int]$Fps = 60,
      [string]$Out = "$env:TEMP\coopplus-shots")
$ff = (Get-Command ffmpeg -ErrorAction SilentlyContinue).Source; if (-not $ff) { $ff = Join-Path $env:LOCALAPPDATA 'Programs\Python\Python313\Scripts\ffmpeg.exe' }
New-Item -ItemType Directory -Force $Out | Out-Null
$idx = if ($Screen -eq 'tv') { 2 } else { 0 }          # DXGI output: 0 = main 300 Hz monitor, 2 = TV
$cols = [math]::Min(6, $Frames); $rows = [math]::Ceiling($Frames / $cols)
$png = Join-Path $Out "$Name.png"
& $ff -hide_banner -loglevel error -y -f lavfi -i "ddagrab=output_idx=${idx}:framerate=${Fps}:draw_mouse=0,hwdownload,format=bgra" `
    -frames:v $Frames -vf "scale=320:-2,tile=${cols}x${rows}:padding=2" -frames:v 1 $png 2>&1
$png
