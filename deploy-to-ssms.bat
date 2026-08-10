@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

echo.
echo ========================================
echo   SSMSBoostPlus Deployment Tool v2.0
echo   (Dynamic Path Detection + Auto Deploy)
echo ========================================
echo.

:: ============================================
:: Step 0: Admin Privilege Check
:: ============================================
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [ERROR] Administrator privileges required!
    echo Please right-click and "Run as administrator"
    echo.
    pause
    exit /b 1
)
echo [OK] Admin privileges confirmed
echo.

:: ============================================
:: Step 0.5: SSMS 22 Process Check
:: ============================================
echo [Step 0.5/9] Checking SSMS 22 process...
:: 增强检查：SSMS.exe + Ssms.exe（注意大小写）+ Microsoft SQL Server Management Studio 主进程
set "SSMS_PROC_FOUND="
for %%P in (SSMS.exe Ssms.exe) do (
    tasklist /FI "IMAGENAME eq %%P" 2>NUL | find /I /N "%%P">NUL
    if not errorlevel 1 set "SSMS_PROC_FOUND=%%P"
)
if defined SSMS_PROC_FOUND (
    echo [ERROR] SSMS 22 process is running: %SSMS_PROC_FOUND%
    echo Please close all SSMS windows first, or DLLs will be locked.
    echo.
    pause
    exit /b 1
)
echo [OK] SSMS 22 not running
echo.

:: ============================================
:: Step 1: Dynamic SSMS 22 Path Detection
:: ============================================
echo [Step 1/9] Detecting SSMS 22 install path...
echo.

:: Auto-derive project root (script dir's parent)
set PROJECT_DIR=%~dp0
if "%PROJECT_DIR:~-1%"=="\" set PROJECT_DIR=%PROJECT_DIR:~0,-1%

echo Project dir: %PROJECT_DIR%
echo Calling Find-SSMS22Path.ps1...
echo.

set FIND_SCRIPT=%PROJECT_DIR%\diagnosis\Find-SSMS22Path.ps1
if not exist "%FIND_SCRIPT%" (
    echo [ERROR] Script not found: %FIND_SCRIPT%
    pause
    exit /b 1
)

:: Call PowerShell script to detect SSMS 22 path
set "TMP_FILE=%TEMP%\SSMS22_Root_%RANDOM%.txt"
powershell -NoProfile -ExecutionPolicy Bypass -File "%FIND_SCRIPT%" -Quiet -OutputFile "%TMP_FILE%" 1>nul 2>nul

:: 【关键】for /F 循环内 set 变量属于"子 shell"，对外层不可见
:: 必须在括号外重新读 TMP_FILE 拿到值，再判断是否成功
set "SSMS_ROOT="
if not exist "%TMP_FILE%" goto :read_tmp_done
set /p SSMS_ROOT=<"%TMP_FILE%"
del "%TMP_FILE%" >nul 2>&1
:read_tmp_done

if not "%SSMS_ROOT%"=="" goto :ssms_root_ok
echo.
    echo [ERROR] SSMS 22 path detection failed!
    echo Please specify install dir manually (e.g. D:\Microsoft SQL Server Management Studio 22\Release)
    echo.

    :: 手动输入校验：必须能找到 SSMS.exe，最多重试 3 次
    set /a MANUAL_RETRY=0
    :manual_input_loop
    set /p SSMS_ROOT="Enter SSMS 22 root directory: "
    if defined SSMS_ROOT goto :input_provided
    echo [ABORT] No path provided, deployment cancelled
    pause
    exit /b 1
:input_provided

    :: 去掉末尾的反斜杠
    if "%SSMS_ROOT:~-1%"=="\" set SSMS_ROOT=%SSMS_ROOT:~0,-1%

    :: 验证 SSMS.exe 是否存在
    set SSMS_EXE_CHECK=
    if exist "%SSMS_ROOT%\Common7\IDE\SSMS.exe" set SSMS_EXE_CHECK=1
    if exist "%SSMS_ROOT%\Release\Common7\IDE\SSMS.exe" set SSMS_EXE_CHECK=1
    if exist "%SSMS_ROOT%\IDE\SSMS.exe" set SSMS_EXE_CHECK=1

    if defined SSMS_EXE_CHECK goto :manual_validated
    set /a MANUAL_RETRY+=1
    echo.
    echo [ERROR] SSMS.exe not found in:
    echo   - %SSMS_ROOT%\Common7\IDE\SSMS.exe
    echo   - %SSMS_ROOT%\Release\Common7\IDE\SSMS.exe
    echo   - %SSMS_ROOT%\IDE\SSMS.exe
    if !MANUAL_RETRY! geq 3 goto :retry_abort
    echo Please try again. (!MANUAL_RETRY!/3)
    echo.
    goto manual_input_loop

:retry_abort
    echo.
    echo [ABORT] Exceeded 3 retry attempts, deployment cancelled
    pause
    exit /b 1

:manual_validated
    echo [OK] Manually specified path validated
goto :ssms_root_ok

:ssms_root_ok
echo [OK] SSMS 22 root: %SSMS_ROOT%
echo.

:: Configure all paths
set SSMS_EXT_DIR=%SSMS_ROOT%\Common7\IDE\Extensions\SSMSBoostPlus
set SSMS_COMMON_DIR=%SSMS_ROOT%\Common7\IDE\commonextensions\SSMSBoostPlus
set CONFIG_FLAG=%SSMS_ROOT%\Common7\IDE\Extensions\extensions.configurationchanged
set SSMS_EXE=%SSMS_ROOT%\Common7\IDE\SSMS.exe
set SOURCE_DIR=%PROJECT_DIR%\src\SSMS\bin\Release\net472
set OBJ_DIR_1=%PROJECT_DIR%\src\SSMS\obj\Release\net472
set OBJ_DIR_2=%PROJECT_DIR%\src\SSMS\obj\Release

echo Source dir: %SOURCE_DIR%
echo Target dir: %SSMS_EXT_DIR%
echo SSMS.exe:   %SSMS_EXE%
echo.

:: ============================================
:: Step 2: Verify Build Output
:: ============================================
echo [Step 2/9] Verifying build output...
echo.

set MISSING=0
for %%F in (SSMSBoostPlus.SSMS.dll SSMSBoostPlus.Core.dll SSMSBoostPlus.AI.dll SSMSBoostPlus.Shared.dll) do (
    if not exist "%SOURCE_DIR%\%%F" (
        echo [ERROR] Missing: %%F
        set MISSING=1
    ) else (
        echo [OK] %%F
    )
)

if !MISSING!==1 (
    echo.
    echo [ABORT] Please build first: dotnet build SSMSBoostPlus.slnx -c Release
    pause
    exit /b 1
)
echo.

:: ============================================
:: Step 3: Clean Conflicting Directories
:: ============================================
echo [Step 3/9] Cleaning conflicting directories...
echo.

if exist "%SSMS_COMMON_DIR%" (
    rmdir /s /q "%SSMS_COMMON_DIR%" >nul
    echo [OK] Removed: %SSMS_COMMON_DIR%
) else (
    echo [OK] No conflicting directory
)
echo.

:: ============================================
:: Step 3.5: Ensure Target Directory Exists
:: ============================================
echo [Step 3.5/9] Ensuring target directory exists...
if not exist "%SSMS_EXT_DIR%" (
    mkdir "%SSMS_EXT_DIR%" >nul
    echo [OK] Target directory created
) else (
    echo [OK] Target directory already exists (files will be updated in-place)
)
echo.

:: ============================================
:: Step 4: Copy Core DLLs (4 + 4 PDB) [40%]
:: ============================================
echo [Step 4/9] Copying core DLLs [40%%]...
echo.

for %%F in (SSMSBoostPlus.SSMS.dll SSMSBoostPlus.Core.dll SSMSBoostPlus.AI.dll SSMSBoostPlus.Shared.dll) do (
    copy /Y "%SOURCE_DIR%\%%F" "%SSMS_EXT_DIR%\" >nul
    if errorlevel 1 (
        echo [ERROR] Copy failed: %%F
        echo   From: %SOURCE_DIR%\%%F
        echo   To:   %SSMS_EXT_DIR%\%%F
        pause
        exit /b 1
    )
    echo [OK] %%F
)

for %%F in (SSMSBoostPlus.SSMS.pdb SSMSBoostPlus.Core.pdb SSMSBoostPlus.AI.pdb SSMSBoostPlus.Shared.pdb) do (
    if exist "%SOURCE_DIR%\%%F" (
        copy /Y "%SOURCE_DIR%\%%F" "%SSMS_EXT_DIR%\" >nul
        if errorlevel 1 (
            echo [WARNING] PDB copy failed: %%F
        ) else (
            echo [OK] %%F
        )
    )
)
echo.

:: ============================================
:: Step 5: Copy Config Files (pkgdef + cto + manifest) [60%]
:: ============================================
echo [Step 5/9] Copying config files [60%%]...
echo.

set PKGDEF_FOUND=0
for %%D in ("%OBJ_DIR_1%" "%OBJ_DIR_2%") do (
    if exist "%%~D\SSMSBoostPlus.SSMS.pkgdef" (
        copy /Y "%%~D\SSMSBoostPlus.SSMS.pkgdef" "%SSMS_EXT_DIR%\" >nul
        if errorlevel 1 (
            echo [ERROR] pkgdef copy failed from %%~D
        ) else (
            echo [OK] SSMSBoostPlus.SSMS.pkgdef
            set PKGDEF_FOUND=1
        )
    )
)
if !PKGDEF_FOUND!==0 echo [WARNING] pkgdef not found in obj/

set CTO_FOUND=0
for %%D in ("%OBJ_DIR_1%" "%OBJ_DIR_2%") do (
    if exist "%%~D\SSMSBoostPlus.cto" (
        copy /Y "%%~D\SSMSBoostPlus.cto" "%SSMS_EXT_DIR%\Menus.ctmenu" >nul
        if errorlevel 1 (
            echo [ERROR] cto copy failed from %%~D
        ) else (
            echo [OK] Menus.ctmenu
            set CTO_FOUND=1
        )
    )
)
if !CTO_FOUND!==0 echo [WARNING] cto not found in obj/

if exist "%PROJECT_DIR%\src\SSMS\source.extension.vsixmanifest" (
    copy /Y "%PROJECT_DIR%\src\SSMS\source.extension.vsixmanifest" "%SSMS_EXT_DIR%\extension.vsixmanifest" >nul
    if errorlevel 1 (
        echo [ERROR] manifest copy failed
    ) else (
        echo [OK] extension.vsixmanifest
    )
) else (
    echo [WARNING] source.extension.vsixmanifest not found
)
echo.

:: ============================================
:: Step 6: Copy Third-Party Dependencies [80%]
:: ============================================
echo [Step 6/9] Copying third-party dependencies [80%%]...
echo.

set DEP_COPIED=0
set DEP_MISSING=

:: Core dependencies (avoid nested () blocks - use subroutine pattern)
call :CopyDepIfExists "DiffPlex.dll"
call :CopyDepIfExists "Serilog.dll"
call :CopyDepIfExists "Serilog.Sinks.File.dll"
call :CopyDepIfExists "Serilog.Enrichers.Environment.dll"
call :CopyDepIfExists "Microsoft.Data.SqlClient.dll"
call :CopyDepIfExists "Newtonsoft.Json.dll"
call :CopyDepIfExists "Microsoft.Bcl.AsyncInterfaces.dll"
call :CopyDepIfExists "Microsoft.Extensions.Configuration.dll"
call :CopyDepIfExists "Microsoft.Extensions.Configuration.Json.dll"
call :CopyDepIfExists "Microsoft.Extensions.Configuration.Binder.dll"
call :CopyDepIfExists "Microsoft.SqlServer.TransactSql.ScriptDom.dll"
call :CopyDepIfExists "System.Net.Http.dll"

:: WPF Markdown rendering libs
call :CopyDepIfExists "Markdig.dll"
call :CopyDepIfExists "Markdown.Xaml.dll"

:: WebView2 runtime deps (dynamically find latest version)
:: 【关键】for /f do (set ...) 是 subshell，set 变量对外层不可见
:: 改用 call 子例程，把 set 操作放在父 shell 上下文
set "WEBVIEW2_NUGET="
set "WEBVIEW2_VER="
for /f "delims=" %%D in ('dir /b /ad "%USERPROFILE%\.nuget\packages\microsoft.web.webview2" 2^>nul') do call :SetWebView2Path "%%D"

if not "%WEBVIEW2_NUGET%"=="" goto :webview2_found
set "DEP_MISSING=!DEP_MISSING! WebView2-*"
goto :webview2_done

:webview2_found
call :CopyWebView2Dep "lib\net462\Microsoft.Web.WebView2.Core.dll"
call :CopyWebView2Dep "lib\net462\Microsoft.Web.WebView2.Wpf.dll"
call :CopyWebView2Dep "runtimes\win-x64\native\WebView2Loader.dll"

:webview2_done

echo [INFO] Copied %DEP_COPIED% dependency files
if "%DEP_MISSING%"=="" goto :dep_warn_done
echo.
echo [WARNING] Missing dependencies:
set "DEP_TEMP=%DEP_MISSING%"

:dep_warn_loop
if "%DEP_TEMP%"=="" goto :dep_warn_done
for /f "tokens=1,*" %%A in ("!DEP_TEMP!") do call :dep_warn_print "%%A" "%%B"
if not "%DEP_TEMP%"=="" goto :dep_warn_loop

:dep_warn_done
echo.

:: ============================================
:: Step 7: Refresh Config Flag [95%]
:: ============================================
echo [Step 7/9] Refreshing config flag [95%%]...
echo.

powershell -NoProfile -Command "(Get-Date -Format 'o') | Set-Content -Path '%CONFIG_FLAG%' -Encoding UTF8" >nul 2>&1
if exist "%CONFIG_FLAG%" (
    echo [OK] Config flag refreshed
) else (
    echo [WARNING] Config flag write failed (non-critical)
)
echo.

:: ============================================
:: Step 8: Force Refresh PkgDef Cache (CRITICAL)
:: ============================================
echo [Step 8/9] Forcing PkgDef cache refresh...
echo.

if not exist "%SSMS_EXE%" (
    echo [WARNING] SSMS.exe not found: %SSMS_EXE%
    echo Please run manually: "%SSMS_EXE%" /setup
) else (
    echo Running: "%SSMS_EXE%" /setup
    echo (Scans all pkgdef and merges to private registry, please wait...)
    echo.
    "%SSMS_EXE%" /setup
    if errorlevel 1 (
        echo.
        echo [WARNING] ssms.exe /setup returned non-zero exit code: %errorlevel%
        echo   This may mean pkgdef was not registered. Extension will not load.
        echo   Please run manually: "%SSMS_EXE%" /setup
    ) else (
        echo [OK] PkgDef cache refreshed
    )
)
echo.

:: ============================================
:: Step 9: Post-Deployment Verification [100%]
:: ============================================
echo [Step 9/9] Verifying deployment [100%%]...
echo.

set VERIFY_FAILED=0
call :VerifyFile "SSMSBoostPlus.SSMS.dll"
call :VerifyFile "SSMSBoostPlus.Core.dll"
call :VerifyFile "SSMSBoostPlus.AI.dll"
call :VerifyFile "SSMSBoostPlus.Shared.dll"

if not exist "%SSMS_EXT_DIR%\SSMSBoostPlus.SSMS.pkgdef" (
    echo [WARNING] pkgdef not deployed
    set VERIFY_FAILED=1
)

if !VERIFY_FAILED!==0 (
    echo [OK] Deployment verification passed
) else (
    echo [ERROR] Deployment verification failed, please check above
)
echo.

:: ============================================
:: Deployment Report
:: ============================================
echo ========================================
echo   Deployment Complete!
echo ========================================
echo.
echo Project dir: %PROJECT_DIR%
echo SSMS 22:     %SSMS_ROOT%
echo Ext dir:     %SSMS_EXT_DIR%
echo.
echo Deployed files:
echo ----------------------------------------
if exist "%SSMS_EXT_DIR%\SSMSBoostPlus.SSMS.dll" echo   [DLL] SSMSBoostPlus.SSMS.dll
if exist "%SSMS_EXT_DIR%\SSMSBoostPlus.Core.dll" echo   [DLL] SSMSBoostPlus.Core.dll
if exist "%SSMS_EXT_DIR%\SSMSBoostPlus.AI.dll" echo   [DLL] SSMSBoostPlus.AI.dll
if exist "%SSMS_EXT_DIR%\SSMSBoostPlus.Shared.dll" echo   [DLL] SSMSBoostPlus.Shared.dll
if exist "%SSMS_EXT_DIR%\SSMSBoostPlus.SSMS.pkgdef" echo   [CFG] SSMSBoostPlus.SSMS.pkgdef
if exist "%SSMS_EXT_DIR%\Menus.ctmenu" echo   [CFG] Menus.ctmenu
if exist "%SSMS_EXT_DIR%\extension.vsixmanifest" echo   [CFG] extension.vsixmanifest
echo ----------------------------------------
echo.
echo Please restart SSMS 22 to load the extension.
echo.
pause

:: ============================================
:: Subroutines (avoid subshell traps)
:: ============================================
:: Copy a single dep file from SOURCE_DIR to SSMS_EXT_DIR
:CopyDepIfExists
if not exist "%SOURCE_DIR%\%~1" goto :CopyDepIfExists_done
copy /Y "%SOURCE_DIR%\%~1" "%SSMS_EXT_DIR%\" >nul
if errorlevel 1 (
    set "DEP_MISSING=!DEP_MISSING! %~1"
) else (
    set /a DEP_COPIED+=1
)
:CopyDepIfExists_done
exit /b

:: Copy a single WebView2 dep file
:CopyWebView2Dep
if not exist "%WEBVIEW2_NUGET%\%~1" exit /b
copy /Y "%WEBVIEW2_NUGET%\%~1" "%SSMS_EXT_DIR%\" >nul
if errorlevel 1 (
    set "DEP_MISSING=!DEP_MISSING! %~1"
) else (
    set /a DEP_COPIED+=1
)
exit /b

:: Verify a single deployed file
:VerifyFile
if exist "%SSMS_EXT_DIR%\%~1" exit /b
echo [ERROR] Verify failed: %~1 not in target
set VERIFY_FAILED=1
exit /b

:: Print one missing dep and update DEP_TEMP
:dep_warn_print
echo   - %~1
if "%~2"=="" set "DEP_TEMP=" & exit /b
set "DEP_TEMP=%~2"
exit /b

:: Set WEBVIEW2_NUGET and WEBVIEW2_VER from a single %%D parameter
:SetWebView2Path
set "WEBVIEW2_NUGET=%USERPROFILE%\.nuget\packages\microsoft.web.webview2\%~1"
set "WEBVIEW2_VER=%~1"
exit /b

