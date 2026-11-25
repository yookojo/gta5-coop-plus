# build_cs.ps1 [-Install]  - compiles the loader and the core. With -Install, copies them into the game:
#   loader -> <GTA>\scripts\CoopPlus.dll   (only needs a game restart if the loader itself changed)
#   core   -> <GTA>\CoopPlus\core\CoopPlusCore.dll  (picked up live within ~1 s, no restart)
param([switch]$Install, [switch]$Loader, [string]$Game = 'A:\GTAV')
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
$out = Join-Path $here 'out'; New-Item -ItemType Directory -Force $out | Out-Null
$fw = 'C:\Windows\Microsoft.NET\Framework64\v4.0.30319'
$csc = 'C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\MSBuild\Current\Bin\Roslyn\csc.exe'
$refs = @("-r:$fw\mscorlib.dll", "-r:$fw\System.dll", "-r:$fw\System.Core.dll", "-r:$fw\System.Drawing.dll", "-r:$fw\System.Windows.Forms.dll", "-r:$Game\ScriptHookVDotNet3.dll")
$coreSrc = @((Join-Path $here 'CoopPlus.cs')) + @(Get-ChildItem (Join-Path $here 'core') -Filter *.cs -ErrorAction SilentlyContinue | ForEach-Object FullName)
foreach ($p in @(@(@(Join-Path $here 'CoopPlusLoader.cs'), 'CoopPlus.dll'), @($coreSrc, 'CoopPlusCore.dll'))) {
    & $csc -nologo -nowarn:CS0618,CS0414 -target:library -optimize+ -langversion:7.3 -nostdlib+ "-out:$out\$($p[1])" @refs @($p[0])
    if ($LASTEXITCODE -ne 0) { throw "compile failed: $($p[0])" }
}
if ($Install) {
    $core = Join-Path $Game 'CoopPlus\core'; New-Item -ItemType Directory -Force $core | Out-Null
    $loaderDst = Join-Path $Game 'scripts\CoopPlus.dll'
    $loaderChanged = $Loader -or -not (Test-Path $loaderDst)
    if ($loaderChanged) { try { Copy-Item "$out\CoopPlus.dll" $loaderDst -Force; 'loader installed (restart GTA to use it)' } catch { 'loader is in use - restart GTA to update it' } }
    Copy-Item "$out\CoopPlusCore.dll" (Join-Path $core 'CoopPlusCore.dll.new') -Force
    Move-Item (Join-Path $core 'CoopPlusCore.dll.new') (Join-Path $core 'CoopPlusCore.dll') -Force
    'core installed - live in about a second'
}
