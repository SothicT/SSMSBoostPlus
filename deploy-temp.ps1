$ErrorActionPreference = "Stop"

$SRC = "d:\172.31.1.44\AI\SSMSBoostPlus\src\SSMS\bin\Release\net472"
$OBJ = "d:\172.31.1.44\AI\SSMSBoostPlus\src\SSMS\obj\Release\net472"
$DST = "D:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\SSMSBoostPlus"
$VS_CACHE = "C:\Users\100019630\AppData\Local\Microsoft\VisualStudio"

Write-Host "=== SSMSBoostPlus 部署脚本 ===" -ForegroundColor Cyan

# 1. 检查 SSMS
$ssms = Get-Process ssms -ErrorAction SilentlyContinue
if ($ssms) {
    Write-Host "[ERROR] SSMS 正在运行，请先关闭！" -ForegroundColor Red
    exit 1
}
Write-Host "[OK] SSMS 已关闭" -ForegroundColor Green

# 2. 复制核心 DLL + PDB
Write-Host "`n[1/6] 复制核心 DLL..." -ForegroundColor Yellow
@("SSMSBoostPlus.SSMS","SSMSBoostPlus.Core","SSMSBoostPlus.AI","SSMSBoostPlus.Shared") | ForEach-Object {
    Copy-Item "$SRC\$_.dll" "$DST\" -Force
    Write-Host "  [OK] $_.dll"
    Copy-Item "$SRC\$_.pdb" "$DST\" -Force -ErrorAction SilentlyContinue
}

# 3. 复制 pkgdef + ctmenu（关键：cto 重命名为 Menus.ctmenu）
Write-Host "`n[2/6] 复制配置文件..." -ForegroundColor Yellow
Copy-Item "$OBJ\SSMSBoostPlus.SSMS.pkgdef" "$DST\" -Force
Write-Host "  [OK] SSMSBoostPlus.SSMS.pkgdef"
Copy-Item "$OBJ\SSMSBoostPlus.cto" "$DST\Menus.ctmenu" -Force
Write-Host "  [OK] Menus.ctmenu (from .cto)"

# 4. 复制第三方依赖
Write-Host "`n[3/6] 复制第三方依赖..." -ForegroundColor Yellow
@("DiffPlex","Serilog","Serilog.Sinks.File","Serilog.Enrichers.Environment",
  "Microsoft.Data.SqlClient","Newtonsoft.Json","Microsoft.Bcl.AsyncInterfaces",
  "Microsoft.Extensions.Configuration","Microsoft.Extensions.Configuration.Json",
  "Microsoft.Extensions.Configuration.Binder","Microsoft.SqlServer.TransactSql.ScriptDom",
  "System.Net.Http","Markdig","Markdown.Xaml") | ForEach-Object {
    $dllPath = "$SRC\$_.dll"
    if (Test-Path $dllPath) {
        Copy-Item $dllPath "$DST\" -Force
        Write-Host "  [OK] $_.dll"
    }
}

# WebView2 依赖
$wv2 = "$env:USERPROFILE\.nuget\packages\microsoft.web.webview2\1.0.2903.40"
if (Test-Path $wv2) {
    @("lib\net462\Microsoft.Web.WebView2.Core.dll","lib\net462\Microsoft.Web.WebView2.Wpf.dll",
      "runtimes\win-x64\native\WebView2Loader.dll","runtimes\win-x86\native\WebView2Loader.dll") | ForEach-Object {
        $path = "$wv2\$_"
        if (Test-Path $path) {
            Copy-Item $path "$DST\" -Force
            Write-Host "  [OK] $(Split-Path $_ -Leaf)"
        }
    }
}

# 5. 清除 SSMS 缓存（关键！）
Write-Host "`n[4/6] 清除 SSMS 缓存..." -ForegroundColor Yellow
$cacheDir = Get-ChildItem $VS_CACHE -Directory | Where-Object { $_.Name -like "18.0_*" } | Select-Object -First 1
if ($cacheDir) {
    $cachePath = $cacheDir.FullName
    $prBin = "$cachePath\privateregistry.bin"
    if (Test-Path $prBin) {
        Copy-Item $prBin "$prBin.bak" -Force
        Remove-Item $prBin -Force
        Write-Host "  [OK] privateregistry.bin 已删除（已备份）" -ForegroundColor Green
    }
    $cmCache = "$cachePath\ComponentModelCache"
    if (Test-Path $cmCache) {
        Remove-Item "$cmCache\*" -Recurse -Force -ErrorAction SilentlyContinue
        Write-Host "  [OK] ComponentModelCache 已清除" -ForegroundColor Green
    }
} else {
    Write-Host "  [WARNING] 未找到 VS 缓存目录" -ForegroundColor Yellow
}

# 6. 刷新配置标记
Write-Host "`n[5/6] 刷新配置标记..." -ForegroundColor Yellow
$configFlag = "D:\Program Files\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\extensions.configurationchanged"
Set-Content -Path $configFlag -Value "$(Get-Date)"
Write-Host "  [OK] 配置标记已刷新" -ForegroundColor Green

# 7. 验证
Write-Host "`n[6/6] 验证部署..." -ForegroundColor Yellow
$asm = [System.Reflection.Assembly]::LoadFile("$DST\SSMSBoostPlus.SSMS.dll")
$resNames = $asm.GetManifestResourceNames()
if ($resNames -contains "Menus.ctmenu") {
    Write-Host "  [OK] DLL 包含 Menus.ctmenu 嵌入资源" -ForegroundColor Green
} else {
    Write-Host "  [WARNING] DLL 未包含 Menus.ctmenu 嵌入资源" -ForegroundColor Yellow
}
$ctmenuSize = (Get-Item "$DST\Menus.ctmenu").Length
Write-Host "  [OK] Menus.ctmenu 文件: $ctmenuSize bytes" -ForegroundColor Green
Write-Host "  [OK] pkgdef: $(Test-Path "$DST\SSMSBoostPlus.SSMS.pkgdef")" -ForegroundColor Green

Write-Host "`n=== 部署完成！请启动 SSMS 22 测试 ===" -ForegroundColor Cyan
