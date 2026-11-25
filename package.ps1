# package.ps1 - builds everything and makes dist\CoopPlus-<version>.zip (install.ps1 + README + files\)
param([string]$Version = (Get-Date -Format 'yyyy.MM.dd'))
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $MyInvocation.MyCommand.Path
cmd /c ""$(Join-Path $root 'src\build.bat')" 2>nul" | Out-Null; if ($LASTEXITCODE -ne 0) { throw 'C++ build failed' }
& (Join-Path $root 'src\build_cs.ps1') | Out-Null
$stage = Join-Path $root "dist\CoopPlus-$Version"; Remove-Item $stage -Recurse -Force -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $stage 'files') | Out-Null
foreach ($f in 'TwoScreens.asi', 'CoopPlus.dll', 'CoopPlusCore.dll', 'PlayGTAV.exe') { Copy-Item (Join-Path $root "src\out\$f") (Join-Path $stage 'files') }
Copy-Item (Join-Path $root 'config\CoopPlus.ini') (Join-Path $stage 'files')
Copy-Item (Join-Path $root 'install.ps1'), (Join-Path $root 'README.md'), (Join-Path $root 'LICENSE') $stage
$zip = "$stage.zip"; Remove-Item $zip -ErrorAction SilentlyContinue
Compress-Archive -Path "$stage\*" -DestinationPath $zip
$zip
