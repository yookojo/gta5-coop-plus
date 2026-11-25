@echo off
call "C:\Program Files (x86)\Microsoft Visual Studio\2022\BuildTools\Common7\Tools\VsDevCmd.bat" -arch=x64 -host_arch=x64 >nul || exit /b 1
cd /d "%~dp0"
if not exist out mkdir out
lib /nologo /def:ScriptHookV.def /machine:x64 /out:out\ScriptHookV.lib || exit /b 1
cl /nologo /O2 /MT /EHsc /W4 /std:c++17 /LD TwoScreens.cpp /Foout\ /Fdout\ /Feout\TwoScreens.asi /link out\ScriptHookV.lib || exit /b 1
cl /nologo /O2 /MT /EHsc outputs.cpp /Foout\ /Feout\outputs.exe || exit /b 1
cl /nologo /O2 /MT /EHsc /DUNICODE /D_UNICODE PlayGTAV.cpp /Foout\ /Feout\PlayGTAV.exe /link /SUBSYSTEM:WINDOWS advapi32.lib || exit /b 1
echo BUILD OK
