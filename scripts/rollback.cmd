@echo off
REM ============================================================
REM UTscan 回滚脚本 —— 恢复最近的 UTscan.exe.bak-v<版本> 备份
REM 用法：双击运行，自动选择最新备份恢复。
REM ============================================================
setlocal enabledelayedexpansion

set "APP_DIR=%~dp0"
set "LOG=%APP_DIR%update.log"

echo ============================================================
echo  UTscan 回滚工具
echo ============================================================
echo.

REM ---- 1. 查找最新备份（按名称倒序取第一个）----
set "BACKUP="
for /f "delims=" %%f in ('dir /b /o-n "%APP_DIR%UTscan.exe.bak-v*" 2^>nul') do (
    if "!BACKUP!"=="" set "BACKUP=%%f"
)

if "%BACKUP%"=="" (
    echo [错误] 未找到任何备份文件（UTscan.exe.bak-v*）。
    echo 无法回滚。
    pause
    exit /b 1
)

echo 找到备份：%BACKUP%
echo 将用其覆盖当前 UTscan.exe。
echo.
choice /C YN /M "确认回滚 [Y/N]"
if errorlevel 2 (
    echo 已取消。
    exit /b 0
)

REM ---- 2. 检查软件未运行 ----
tasklist /FI "IMAGENAME eq UTscan.exe" 2>nul | find /I "UTscan.exe" >nul
if not errorlevel 1 (
    echo [错误] UTscan.exe 正在运行，请先关闭软件再回滚。
    pause
    exit /b 1
)

REM ---- 3. 回滚：先备份当前（防止再想回滚到当前版）----
set "CUR_BAK=%APP_DIR%UTscan.exe.pre-rollback"
if exist "%APP_DIR%UTscan.exe" (
    copy /Y "%APP_DIR%UTscan.exe" "%CUR_BAK%" >nul 2>&1
)

REM ---- 4. 恢复 ----
copy /Y "%APP_DIR%%BACKUP%" "%APP_DIR%UTscan.exe" >nul
if errorlevel 1 (
    echo [错误] 回滚失败（文件可能被占用）。
    pause
    exit /b 1
)

REM ---- 5. 尝试同步 version.json（从备份名提取版本）----
set "VER=%BACKUP:~13%"
set "VER=%VER:.bak=%"
if exist "%APP_DIR%version.json" (
    copy /Y "%APP_DIR%version.json" "%APP_DIR%version.json.pre-rollback" >nul 2>&1
)
echo {"version":"%VER%","note":"rolled-back"} > "%APP_DIR%version.json" 2>nul

echo [%date% %time%] 回滚完成：恢复 %BACKUP% >> "%LOG%"

echo.
echo ============ 回滚完成 ============
echo   已恢复：%BACKUP%
echo   当前 exe 已另存为 UTscan.exe.pre-rollback
echo.
echo 请启动 UTscan.exe 验证。若回滚版本也不可用，请立即联系开发人员。
echo.
pause
exit /b 0
