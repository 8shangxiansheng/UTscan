@echo off
REM ============================================================
REM UTscan 工控机离线更新脚本
REM 用法：把新版 UTscan.exe 与 version.json 放到本目录 _update\ 子文件夹，
REM       然后双击本脚本（或命令行运行 update.cmd）。
REM
REM 安全原则（铁律）：
REM   1. 只替换 UTscan.exe —— 绝不覆盖 hardware.json / %AppData%\UTscan\ 配置
REM   2. 校验先行：新版 exe 的 SHA256 必须与 version.json 一致，否则中止
REM   3. 原子替换：先复制为临时文件并校验，成功后才替换正式文件
REM   4. 自动备份：旧版 exe 保留为 UTscan.exe.bak-v<旧版本>，可回滚
REM   5. 全程日志：写入 update.log
REM ============================================================
setlocal enabledelayedexpansion

set "APP_DIR=%~dp0"
set "UPDATE_DIR=%APP_DIR%_update"
set "LOG=%APP_DIR%update.log"
set "NEW_EXE=%UPDATE_DIR%\UTscan.exe"
set "NEW_VER=%UPDATE_DIR%\version.json"

echo ============================================================
echo  UTscan 更新工具
echo ============================================================
echo.

REM ---- 0. 检查更新包存在 ----
if not exist "%NEW_EXE%" (
    echo [错误] 未找到 %NEW_EXE%
    echo 请将新版 UTscan.exe 和 version.json 放入 _update\ 文件夹后重试。
    echo.
    pause
    exit /b 1
)
if not exist "%NEW_VER%" (
    echo [错误] 未找到 %NEW_VER%（新版 version.json 缺失，无法校验）
    pause
    exit /b 1
)

REM ---- 1. 读取新版本号 ----
set "NEW_VERSION="
for /f "usebackq tokens=2 delims=:,}" %%i in (`findstr /i "version" "%NEW_VER%" 2^>nul`) do set "NEW_VERSION=%%~i"
set "NEW_VERSION=%NEW_VERSION: =%"
set "NEW_VERSION=%NEW_VERSION:"=%"
if "%NEW_VERSION%"=="" set "NEW_VERSION=unknown"

REM ---- 2. 读取当前版本号（存在旧 version.json 时）----
set "OLD_VERSION=unknown"
if exist "%APP_DIR%version.json" (
    for /f "usebackq tokens=2 delims=:,}" %%i in (`findstr /i "version" "%APP_DIR%version.json" 2^>nul`) do set "OLD_VERSION=%%~i"
    set "OLD_VERSION=!OLD_VERSION: =!"
    set "OLD_VERSION=!OLD_VERSION:"=!"
)

echo 当前版本 : %OLD_VERSION%
echo 新版版本 : %NEW_VERSION%
echo 更新目录 : %UPDATE_DIR%
echo.
echo 即将：备份当前 exe 并替换为新版。
echo 注意：hardware.json 与 %AppData%\UTscan\ 配置不受影响。
echo.
choice /C YN /M "确认执行更新 [Y/N]"
if errorlevel 2 (
    echo 已取消。
    exit /b 0
)

REM ---- 3. 检查软件未运行（exe 运行中无法覆盖）----
tasklist /FI "IMAGENAME eq UTscan.exe" 2>nul | find /I "UTscan.exe" >nul
if not errorlevel 1 (
    echo [错误] UTscan.exe 正在运行，请先关闭软件再执行更新。
    echo 保存当前数据后退出软件，然后重新运行本脚本。
    pause
    exit /b 1
)

REM ---- 4. 校验新版 exe SHA256（与 version.json 比对）----
set "EXPECTED_SHA="
for /f "usebackq tokens=2 delims=:,}" %%i in (`findstr /i "exeSha256" "%NEW_VER%" 2^>nul`) do set "EXPECTED_SHA=%%~i"
set "EXPECTED_SHA=%EXPECTED_SHA: =%"
set "EXPECTED_SHA=%EXPECTED_SHA:"=%"

if not "%EXPECTED_SHA%"=="" (
    echo [校验] 计算新版 UTscan.exe SHA256 ...
    set "ACTUAL_SHA="
    for /f "skip=1 tokens=*" %%h in ('certutil -hashfile "%NEW_EXE%" SHA256 2^>nul') do (
        if "!ACTUAL_SHA!"=="" set "ACTUAL_SHA=%%h"
    )
    set "ACTUAL_SHA=!ACTUAL_SHA: =!"

    if /i not "!ACTUAL_SHA!"=="%EXPECTED_SHA%" (
        echo [错误] SHA256 校验失败！
        echo   期望 : %EXPECTED_SHA%
        echo   实际 : !ACTUAL_SHA!
        echo 新版文件可能已损坏（U 盘拷贝中断/网络传输出错），已中止。
        echo 请重新拷贝更新包后再试。
        pause
        exit /b 1
    )
    echo [校验] SHA256 一致，文件完整。
) else (
    echo [警告] version.json 未含 exeSha256，跳过哈希校验（建议重新生成更新包）。
)

REM ---- 5. 备份当前 exe ----
set "BACKUP=%APP_DIR%UTscan.exe.bak-v%OLD_VERSION%"
if exist "%APP_DIR%UTscan.exe" (
    copy /Y "%APP_DIR%UTscan.exe" "%BACKUP%" >nul
    if errorlevel 1 (
        echo [错误] 备份当前 exe 失败，已中止（不冒险替换）。
        pause
        exit /b 1
    )
    echo [备份] 当前版本已备份为 UTscan.exe.bak-v%OLD_VERSION%
)

REM ---- 6. 原子替换：先复制为临时文件 ----
set "TMP_EXE=%APP_DIR%UTscan.exe.new"
copy /Y "%NEW_EXE%" "%TMP_EXE%" >nul
if errorlevel 1 (
    echo [错误] 复制新版 exe 失败，已中止（旧版未受影响）。
    pause
    exit /b 1
)

REM ---- 7. 校验临时副本（防止复制过程损坏）----
if not "%EXPECTED_SHA%"=="" (
    set "TMP_SHA="
    for /f "skip=1 tokens=*" %%h in ('certutil -hashfile "%TMP_EXE%" SHA256 2^>nul') do (
        if "!TMP_SHA!"=="" set "TMP_SHA=%%h"
    )
    set "TMP_SHA=!TMP_SHA: =!"
    if /i not "!TMP_SHA!"=="%EXPECTED_SHA%" (
        del "%TMP_EXE%" >nul 2>&1
        echo [错误] 复制后校验失败，已删除临时文件并中止（旧版未受影响）。
        pause
        exit /b 1
    )
)

REM ---- 8. 正式替换 ----
move /Y "%TMP_EXE%" "%APP_DIR%UTscan.exe" >nul
if errorlevel 1 (
    echo [错误] 替换 UTscan.exe 失败。
    echo 正在尝试恢复备份 ...
    if exist "%BACKUP%" copy /Y "%BACKUP%" "%APP_DIR%UTscan.exe" >nul
    pause
    exit /b 1
)

REM ---- 9. 同步 version.json（让程序显示新版本号）----
copy /Y "%NEW_VER%" "%APP_DIR%version.json" >nul 2>&1

REM ---- 10. 写日志 ----
echo [%date% %time%] 更新完成：v%OLD_VERSION% → v%NEW_VERSION% >> "%LOG%"

echo.
echo ============ 更新完成 ============
echo   v%OLD_VERSION%  →  v%NEW_VERSION%
echo   旧版备份：UTscan.exe.bak-v%OLD_VERSION%
echo   更新日志：update.log
echo.
echo 请启动 UTscan.exe 验证：
echo   1. 标题栏显示新版本号
echo   2. 连接 ZMC/DPR500 正常、A 扫有波形
echo   3. 如异常：运行 rollback.cmd 恢复旧版
echo.
pause
exit /b 0
