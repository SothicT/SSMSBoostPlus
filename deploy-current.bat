@echo off
chcp 65001 >nul
setlocal enabledelayedexpansion

echo.
echo ========================================
echo   SSMSBoostPlus Deployment Tool
echo   (Current Workspace)
echo ========================================
echo.

:: Check admin privileges
net session >nul 2>&1
if %errorLevel% neq 0 (
    echo [ERROR] 需要管理员权限！
    echo 请以管理员身份运行此脚本。
    echo.
    pause
    exit /b 1
)

:: Configure paths - 使用当前工作区路径
set PROJECT_DIR=d:\172.31.1.44\AI\SSMSBoostPlus
set SOURCE_DIR=%PROJECT_DIR%\src\SSMS\bin\Release\net472
set OBJ_DIR=%PROJECT_DIR%\src\SSMS\obj\Release\net472
set SSMS_EXT_DIR=D:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\SSMSBoostPlus
set SSMS_COMMON_DIR=D:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\commonextensions\SSMSBoostPlus
set CONFIG_FLAG=D:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\extensions.configurationchanged
set SSMS_EXE=D:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\SSMS.exe

echo 项目目录: %PROJECT_DIR%
echo 编译输出: %SOURCE_DIR%
echo 目标目录: %SSMS_EXT_DIR%
echo.

:: Step 1: Check source files
echo [Step 1/7] 检查编译产物...
echo.

set MISSING=0

if not exist "%SOURCE_DIR%\SSMSBoostPlus.SSMS.dll" (
    echo [ERROR] 缺少: SSMSBoostPlus.SSMS.dll
    set MISSING=1
)
if not exist "%SOURCE_DIR%\SSMSBoostPlus.Core.dll" (
    echo [ERROR] 缺少: SSMSBoostPlus.Core.dll
    set MISSING=1
)
if not exist "%SOURCE_DIR%\SSMSBoostPlus.AI.dll" (
    echo [ERROR] 缺少: SSMSBoostPlus.AI.dll
    set MISSING=1
)
if not exist "%SOURCE_DIR%\SSMSBoostPlus.Shared.dll" (
    echo [ERROR] 缺少: SSMSBoostPlus.Shared.dll
    set MISSING=1
)

if %MISSING%==1 (
    echo 请先编译项目。
    pause
    exit /b 1
)

echo [OK] SSMSBoostPlus.SSMS.dll
echo [OK] SSMSBoostPlus.Core.dll
echo [OK] SSMSBoostPlus.AI.dll
echo [OK] SSMSBoostPlus.Shared.dll
echo.

:: Step 2: Remove conflicting directory
echo [Step 2/7] 清理冲突目录...
echo.

if exist "%SSMS_COMMON_DIR%" (
    rmdir /s /q "%SSMS_COMMON_DIR%"
    echo [OK] 已删除冲突目录: %SSMS_COMMON_DIR%
) else (
    echo [OK] 无冲突目录需要清理
)
echo.

:: Step 3: Create target directory
echo [Step 3/7] 创建目标目录...
echo.

if not exist "%SSMS_EXT_DIR%" (
    mkdir "%SSMS_EXT_DIR%"
    echo [OK] 已创建: %SSMS_EXT_DIR%
) else (
    echo [OK] 目标目录已存在: %SSMS_EXT_DIR%
)
echo.

:: Step 4: Copy core DLL files
echo [Step 4/7] 复制核心 DLL 文件 [40%%]...
echo.

copy /Y "%SOURCE_DIR%\SSMSBoostPlus.SSMS.dll" "%SSMS_EXT_DIR%\" >nul
echo [OK] SSMSBoostPlus.SSMS.dll

copy /Y "%SOURCE_DIR%\SSMSBoostPlus.Core.dll" "%SSMS_EXT_DIR%\" >nul
echo [OK] SSMSBoostPlus.Core.dll

copy /Y "%SOURCE_DIR%\SSMSBoostPlus.AI.dll" "%SSMS_EXT_DIR%\" >nul
echo [OK] SSMSBoostPlus.AI.dll

copy /Y "%SOURCE_DIR%\SSMSBoostPlus.Shared.dll" "%SSMS_EXT_DIR%\" >nul
echo [OK] SSMSBoostPlus.Shared.dll

if exist "%SOURCE_DIR%\SSMSBoostPlus.SSMS.pdb" (
    copy /Y "%SOURCE_DIR%\SSMSBoostPlus.SSMS.pdb" "%SSMS_EXT_DIR%\" >nul
    echo [OK] SSMSBoostPlus.SSMS.pdb
)
if exist "%SOURCE_DIR%\SSMSBoostPlus.Core.pdb" (
    copy /Y "%SOURCE_DIR%\SSMSBoostPlus.Core.pdb" "%SSMS_EXT_DIR%\" >nul
    echo [OK] SSMSBoostPlus.Core.pdb
)
if exist "%SOURCE_DIR%\SSMSBoostPlus.AI.pdb" (
    copy /Y "%SOURCE_DIR%\SSMSBoostPlus.AI.pdb" "%SSMS_EXT_DIR%\" >nul
    echo [OK] SSMSBoostPlus.AI.pdb
)
if exist "%SOURCE_DIR%\SSMSBoostPlus.Shared.pdb" (
    copy /Y "%SOURCE_DIR%\SSMSBoostPlus.Shared.pdb" "%SSMS_EXT_DIR%\" >nul
    echo [OK] SSMSBoostPlus.Shared.pdb
)
echo.

:: Step 5: Copy configuration files
echo [Step 5/7] 复制配置文件 [60%%]...
echo.

if exist "%OBJ_DIR%\SSMSBoostPlus.SSMS.pkgdef" (
    copy /Y "%OBJ_DIR%\SSMSBoostPlus.SSMS.pkgdef" "%SSMS_EXT_DIR%\" >nul
    echo [OK] SSMSBoostPlus.SSMS.pkgdef
) else (
    echo [WARNING] pkgdef 文件未找到
)

if exist "%OBJ_DIR%\SSMSBoostPlus.cto" (
    copy /Y "%OBJ_DIR%\SSMSBoostPlus.cto" "%SSMS_EXT_DIR%\Menus.ctmenu" >nul
    echo [OK] Menus.ctmenu (从 cto 复制)
) else (
    echo [WARNING] cto 文件未找到
)

if exist "%PROJECT_DIR%\src\SSMS\source.extension.vsixmanifest" (
    copy /Y "%PROJECT_DIR%\src\SSMS\source.extension.vsixmanifest" "%SSMS_EXT_DIR%\extension.vsixmanifest" >nul
    echo [OK] extension.vsixmanifest
)
echo.

:: Step 6: Copy dependencies
echo [Step 6/7] 复制依赖项 [80%%]...
echo.

:: 核心依赖
if exist "%SOURCE_DIR%\DiffPlex.dll" copy /Y "%SOURCE_DIR%\DiffPlex.dll" "%SSMS_EXT_DIR%\" >nul
if exist "%SOURCE_DIR%\Serilog.dll" copy /Y "%SOURCE_DIR%\Serilog.dll" "%SSMS_EXT_DIR%\" >nul
if exist "%SOURCE_DIR%\Serilog.Sinks.File.dll" copy /Y "%SOURCE_DIR%\Serilog.Sinks.File.dll" "%SSMS_EXT_DIR%\" >nul
if exist "%SOURCE_DIR%\Microsoft.Data.SqlClient.dll" copy /Y "%SOURCE_DIR%\Microsoft.Data.SqlClient.dll" "%SSMS_EXT_DIR%\" >nul
if exist "%SOURCE_DIR%\Newtonsoft.Json.dll" copy /Y "%SOURCE_DIR%\Newtonsoft.Json.dll" "%SSMS_EXT_DIR%\" >nul

:: 日志增强 + 异步接口
if exist "%SOURCE_DIR%\Serilog.Enrichers.Environment.dll" copy /Y "%SOURCE_DIR%\Serilog.Enrichers.Environment.dll" "%SSMS_EXT_DIR%\" >nul
if exist "%SOURCE_DIR%\Microsoft.Bcl.AsyncInterfaces.dll" copy /Y "%SOURCE_DIR%\Microsoft.Bcl.AsyncInterfaces.dll" "%SSMS_EXT_DIR%\" >nul

:: 配置框架
if exist "%SOURCE_DIR%\Microsoft.Extensions.Configuration.dll" copy /Y "%SOURCE_DIR%\Microsoft.Extensions.Configuration.dll" "%SSMS_EXT_DIR%\" >nul
if exist "%SOURCE_DIR%\Microsoft.Extensions.Configuration.Json.dll" copy /Y "%SOURCE_DIR%\Microsoft.Extensions.Configuration.Json.dll" "%SSMS_EXT_DIR%\" >nul
if exist "%SOURCE_DIR%\Microsoft.Extensions.Configuration.Binder.dll" copy /Y "%SOURCE_DIR%\Microsoft.Extensions.Configuration.Binder.dll" "%SSMS_EXT_DIR%\" >nul

:: T-SQL 解析器
if exist "%SOURCE_DIR%\Microsoft.SqlServer.TransactSql.ScriptDom.dll" copy /Y "%SOURCE_DIR%\Microsoft.SqlServer.TransactSql.ScriptDom.dll" "%SSMS_EXT_DIR%\" >nul

:: HTTP 基础库
if exist "%SOURCE_DIR%\System.Net.Http.dll" copy /Y "%SOURCE_DIR%\System.Net.Http.dll" "%SSMS_EXT_DIR%\" >nul

:: WPF Markdown 渲染库
if exist "%SOURCE_DIR%\Markdig.dll" copy /Y "%SOURCE_DIR%\Markdig.dll" "%SSMS_EXT_DIR%\" >nul
if exist "%SOURCE_DIR%\Markdown.Xaml.dll" copy /Y "%SOURCE_DIR%\Markdown.Xaml.dll" "%SSMS_EXT_DIR%\" >nul

:: WebView2 运行时依赖
set WEBVIEW2_NUGET=%USERPROFILE%\.nuget\packages\microsoft.web.webview2\1.0.2903.40
if exist "%WEBVIEW2_NUGET%\lib\net462\Microsoft.Web.WebView2.Core.dll" copy /Y "%WEBVIEW2_NUGET%\lib\net462\Microsoft.Web.WebView2.Core.dll" "%SSMS_EXT_DIR%\" >nul
if exist "%WEBVIEW2_NUGET%\lib\net462\Microsoft.Web.WebView2.Wpf.dll" copy /Y "%WEBVIEW2_NUGET%\lib\net462\Microsoft.Web.WebView2.Wpf.dll" "%SSMS_EXT_DIR%\" >nul
if exist "%WEBVIEW2_NUGET%\runtimes\win-x64\native\WebView2Loader.dll" copy /Y "%WEBVIEW2_NUGET%\runtimes\win-x64\native\WebView2Loader.dll" "%SSMS_EXT_DIR%\" >nul
if exist "%WEBVIEW2_NUGET%\runtimes\win-x86\native\WebView2Loader.dll" copy /Y "%WEBVIEW2_NUGET%\runtimes\win-x86\native\WebView2Loader.dll" "%SSMS_EXT_DIR%\" >nul

echo [OK] 依赖项复制完成
echo.

:: Step 7: Refresh configuration
echo [Step 7/7] 刷新配置标记 [95%%]...
echo.

echo %DATE% %TIME% > "%CONFIG_FLAG%"
echo [OK] 配置标记已刷新
echo.

:: ============================================
:: Deployment complete
:: ============================================
echo ========================================
echo   部署完成! [100%%]
echo ========================================
echo.
echo 扩展目录:
echo   %SSMS_EXT_DIR%
echo.
echo 已安装的文件:
echo ----------------------------------------
if exist "%SSMS_EXT_DIR%\SSMSBoostPlus.SSMS.dll" echo [OK] SSMSBoostPlus.SSMS.dll
if exist "%SSMS_EXT_DIR%\SSMSBoostPlus.Core.dll" echo [OK] SSMSBoostPlus.Core.dll
if exist "%SSMS_EXT_DIR%\SSMSBoostPlus.AI.dll" echo [OK] SSMSBoostPlus.AI.dll
if exist "%SSMS_EXT_DIR%\SSMSBoostPlus.Shared.dll" echo [OK] SSMSBoostPlus.Shared.dll
if exist "%SSMS_EXT_DIR%\SSMSBoostPlus.SSMS.pkgdef" echo [OK] SSMSBoostPlus.SSMS.pkgdef
if exist "%SSMS_EXT_DIR%\Menus.ctmenu" echo [OK] Menus.ctmenu
if exist "%SSMS_EXT_DIR%\extension.vsixmanifest" echo [OK] extension.vsixmanifest
echo ----------------------------------------
echo.
echo 请重启 SSMS 22 以加载扩展。
echo.
pause