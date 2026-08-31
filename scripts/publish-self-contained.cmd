@echo off
REM =============================================================
REM UTscan publish script (multi-file, green portable, offline)
REM Usage:   publish-self-contained.cmd [version]
REM          e.g.  publish-self-contained.cmd 1.2.0
REM Output:  dist\UTscan-win-x86\  (self-contained multi-file dir)
REM          + manifest.json (per-file SHA256 list, consumed by in-app updater)
REM          + version.json  (current version info)
REM Notes:
REM   [!] RID is win-x86 on purpose: hardware DLLs (zauxdll/zmotion/
REM       spcm_win32/JSR_Common3264) are 32-bit; ZMC samples are 32-bit.
REM       Runs on x64 hosts via WoW64. Do NOT change RID without
REM       matching x64 DLLs (PlatformTarget=x86 conflicts otherwise).
REM   [!] This file is ASCII-only on purpose: industrial PCs run cmd
REM       under ANSI code pages (GBK); non-ASCII comments garble and
REM       can break parsing. Non-ASCII asset paths live in
REM       finalize-dist.ps1 (UTF-8 BOM, read correctly by PowerShell).
REM   Multi-file layout (PublishSingleFile=false) per update design:
REM       full-directory overlay + per-file SHA256 diff skip.
REM =============================================================
setlocal

set ROOT=%~dp0..
set DOTNET=C:\Users\86185\.dotnet\dotnet.exe
set RUNTIME=win-x86
set CONFIG=Release
set OUT=%ROOT%\dist\UTscan-%RUNTIME%

REM Version: arg1, default = today YYYYMMDD
set VERSION=%~1
if "%VERSION%"=="" set VERSION=%date:~0,4%%date:~5,2%%date:~8,2%

echo [1/5] Restore...
"%DOTNET%" restore "%ROOT%\UTscan.sln" || goto :error

echo [2/5] Run unit tests...
"%DOTNET%" test "%ROOT%\tests\UTscan.Tests\UTscan.Tests.csproj" -c %CONFIG% --nologo -v q || goto :error

echo [3/5] Verify native DLLs present (C5 gate)...
if not exist "%ROOT%\src\UTscan\Hardware\NativeDlls\zmotion.dll"    goto :error_dll
if not exist "%ROOT%\src\UTscan\Hardware\NativeDlls\zauxdll.dll"    goto :error_dll
if not exist "%ROOT%\src\UTscan\Hardware\NativeDlls\spcm_win32.dll" goto :error_dll
if not exist "%ROOT%\src\UTscan\Hardware\PulseGen\runtimes\win-x64\native\JSR_Common3264.dll" goto :error_dll
if not exist "%ROOT%\src\UTscan\Hardware\PulseGen\runtimes\win-x64\native\DPRIO3.dll" goto :error_dll

echo [4/5] Publish self-contained multi-file (R2R)...
if exist "%OUT%" rd /s /q "%OUT%"
"%DOTNET%" publish "%ROOT%\src\UTscan" ^
    -c %CONFIG% -r %RUNTIME% ^
    --self-contained true ^
    -p:PublishSingleFile=false ^
    -p:PublishReadyToRun=true ^
    -p:DebugType=embedded ^
    -o "%OUT%" || goto :error

echo [5/5] Finalize dist: config/docs/drivers + manifest.json + version.json ...
powershell -NoProfile -ExecutionPolicy Bypass -File "%~dp0finalize-dist.ps1" -Root "%ROOT%" -AppDir "%OUT%" -Version "%VERSION%" || goto :error

echo [6/6] Done.
echo.
echo ===== Summary =====
echo Output     : %OUT%
echo Version    : %VERSION%
echo manifest   : %OUT%\manifest.json  (files+sha256, for in-app updater)
echo version    : %OUT%\version.json   (displayed by the app)
echo Next steps : zip dist\UTscan-%RUNTIME% as update-v%VERSION%.zip,
echo              copy to industrial PC app folder under _update\
echo              then use in-app Help^>Check for updates.
echo.
exit /b 0

:error_dll
echo.
echo ===== FAILED: native hardware DLL missing =====
echo C5 gate: all 5 native DLLs must exist before publish.
echo   zmotion.dll / zauxdll.dll / spcm_win32.dll (Hardware\NativeDlls\)
echo   JSR_Common3264.dll / DPRIO3.dll (Hardware\PulseGen\runtimes\win-x64\native\)
exit /b 1

:error
echo.
echo ===== FAILED =====
exit /b 1
