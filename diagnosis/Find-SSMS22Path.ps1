<#
.SYNOPSIS
    检测 SSMS 22 (SQL Server Management Studio 22) 安装路径

.DESCRIPTION
    6 级分层检测策略，按优先级自动发现 SSMS 22 安装位置：
    Tier 1: 用户配置（%APPDATA%\SSMSBoostPlus\config.json）
    Tier 2: 环境变量（SSMS22_PATH / SSMS_INSTALL_PATH）
    Tier 3: vswhere.exe（VS 官方工具）
    Tier 4: Windows 注册表 Uninstall 节点
    Tier 5: 常见路径扫描（D:/E:/F: 盘）
    Tier 6: 运行进程探测（Get-Process SSMS）

.PARAMETER AsJson
    以 JSON 格式输出结果（便于脚本解析）

.PARAMETER NoPersist
    不写入用户配置文件（仅探测）

.PARAMETER Source
    强制使用指定的检测源（registry/env/vswhere/common_path/running_process）
    默认自动按 Tier 1-6 顺序探测

.PARAMETER Verbose
    启用详细日志

.EXAMPLE
    $root = & .\diagnosis\Find-SSMS22Path.ps1

.EXAMPLE
    & .\diagnosis\Find-SSMS22Path.ps1 -AsJson

.EXAMPLE
    & .\diagnosis\Find-SSMS22Path.ps1 -Source registry -NoPersist -Verbose

.OUTPUTS
    [string] 默认输出 SSMS 22 根目录路径
    [string] -AsJson 时输出 JSON 字符串

.NOTES
    File:     diagnosis/Find-SSMS22Path.ps1
    Author:   AI Assistant
    Created:  2026-07-02
    Project:  SSMSBoostPlus
    Version:  1.0.0
#>

[CmdletBinding()]
param(
    [switch]$AsJson,
    [switch]$NoPersist,
    [switch]$Quiet,                                     # bat 调用模式：抑制所有非错误输出，仅写文件/输出路径
    [string]$OutputFile,                                # 将路径写入指定文件（与 -Quiet 配合使用）
    [ValidateSet('auto', 'user_config', 'env', 'vswhere', 'registry', 'common_path', 'running_process')]
    [string]$Source = 'auto',
    [switch]$VerboseOutput
)

#Requires -Version 5.0
$ErrorActionPreference = 'Stop'

# ==================== 工具函数 ====================

function Write-Log {
    param(
        [string]$Message,
        [string]$Color = 'Cyan'
    )
    # Quiet 模式下完全静默；否则写入 stderr（避免污染 stdout）
    if (-not $Quiet) {
        [Console]::Error.WriteLine($Message)
    }
}

function Write-TierLog {
    param(
        [int]$Tier,
        [string]$Status,
        [string]$Message,
        [string]$Color = 'Cyan'
    )
    if ($Quiet) { return }
    $icon = switch ($Status) {
        'OK'   { '✅' }
        'FAIL' { '❌' }
        'SKIP' { '⏭️' }
        'INFO' { 'ℹ️' }
        default { '·' }
    }
    if ($VerboseOutput -or $Status -ne 'SKIP') {
        [Console]::Error.WriteLine("[Tier $Tier] $icon $Message")
    }
}

function Test-SSMSRoot {
    param([string]$Path)
    if ([string]::IsNullOrWhiteSpace($Path)) { return $false }
    if (-not (Test-Path $Path)) { return $false }

    # 多种可能的 SSMS.exe 位置（适配不同安装结构）
    $ssmsExeCandidates = @(
        (Join-Path $Path 'Common7\IDE\SSMS.exe'),
        (Join-Path $Path 'Release\Common7\IDE\SSMS.exe'),
        (Join-Path $Path 'IDE\SSMS.exe')
    )
    foreach ($exe in $ssmsExeCandidates) {
        if (Test-Path $exe) { return $true }
    }
    return $false
}

function Resolve-SSMSRootFromSSMSExe {
    param([string]$SSMSExePath)
    # SSMS.exe 在 Common7\IDE\SSMS.exe，向上 2 级到 SSMS 22 根目录
    # 例如: .../Microsoft SQL Server Management Studio 22/Release/Common7/IDE/SSMS.exe
    #       -> .../Microsoft SQL Server Management Studio 22/Release/
    $ideDir = Split-Path $SSMSExePath -Parent
    $common7Dir = Split-Path $ideDir -Parent
    $root = Split-Path $common7Dir -Parent
    return $root
}

# ==================== Tier 1: 用户配置 ====================

function Find-UserConfig {
    $configPath = Join-Path $env:APPDATA 'SSMSBoostPlus\config.json'
    if (-not (Test-Path $configPath)) { return $null }

    try {
        $cfg = Get-Content $configPath -Raw -Encoding UTF8 | ConvertFrom-Json
        if ($cfg.ssms_install_path -and (Test-SSMSRoot $cfg.ssms_install_path)) {
            return @{
                Path   = $cfg.ssms_install_path
                Source = 'user_config'
            }
        }
    } catch {
        if ($VerboseOutput) {
            [Console]::Error.WriteLine("配置文件解析失败: $_")
        }
    }
    return $null
}

# ==================== Tier 2: 环境变量 ====================

function Find-EnvVar {
    foreach ($envVar in @('SSMS22_PATH', 'SSMS_INSTALL_PATH')) {
        $val = [Environment]::GetEnvironmentVariable($envVar)
        if ($val -and (Test-SSMSRoot $val)) {
            return @{
                Path   = $val
                Source = "env_$envVar"
            }
        }
    }
    return $null
}

# ==================== Tier 3: vswhere.exe ====================

function Find-VSWhere {
    $vswhere = Join-Path ${env:ProgramFiles(x86)} 'Microsoft Visual Studio\Installer\vswhere.exe'
    if (-not (Test-Path $vswhere)) { return $null }

    try {
        $vsPath = & $vswhere -prerelease -products * -property installationPath 2>$null
        if ($vsPath) {
            $candidates = @(
                (Join-Path $vsPath 'Common7\IDE'),
                (Join-Path $vsPath '..\Common7\IDE')
            )
            foreach ($c in $candidates) {
                $resolved = (Resolve-Path $c -ErrorAction SilentlyContinue).Path
                if ($resolved -and (Test-Path (Join-Path $resolved 'SSMS.exe'))) {
                    $root = Resolve-SSMSRootFromSSMSExe (Join-Path $resolved 'SSMS.exe')
                    if (Test-SSMSRoot $root) {
                        return @{
                            Path   = $root
                            Source = 'vswhere'
                        }
                    }
                }
            }
        }
    } catch {
        if ($VerboseOutput) {
            [Console]::Error.WriteLine("vswhere 调用失败: $_")
        }
    }
    return $null
}

# ==================== Tier 4: 注册表 ====================

function Find-Registry {
    $regRoots = @(
        'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\*',
        'HKLM:\SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall\*'
    )
    foreach ($root in $regRoots) {
        $apps = Get-ItemProperty $root -ErrorAction SilentlyContinue |
                Where-Object { $_.DisplayName -match 'SQL Server Management Studio.*22' }
        foreach ($app in $apps) {
            $installDir = $app.InstallLocation
            if (-not $installDir) { continue }

            # InstallLocation 可能指向 Common7\IDE 或 Release 根目录
            $candidates = @(
                $installDir,
                (Join-Path $installDir 'Common7\IDE'),
                (Split-Path $installDir -Parent)
            )
            foreach ($c in $candidates) {
                $resolved = (Resolve-Path $c -ErrorAction SilentlyContinue).Path
                if ($resolved -and (Test-Path (Join-Path $resolved 'SSMS.exe'))) {
                    $root = Resolve-SSMSRootFromSSMSExe (Join-Path $resolved 'SSMS.exe')
                    if (Test-SSMSRoot $root) {
                        return @{
                            Path   = $root
                            Source = 'registry'
                        }
                    }
                }
            }
        }
    }
    return $null
}

# ==================== Tier 5: 常见路径扫描 ====================

function Find-CommonPath {
    $rootDrives = @('D:\', 'C:\Program Files\', 'E:\', 'F:\', 'C:\')
    $pattern = 'Microsoft SQL Server Management Studio 22'

    foreach ($drive in $rootDrives) {
        if (-not (Test-Path $drive)) { continue }
        try {
            $dirs = Get-ChildItem $drive -Directory -Filter $pattern -ErrorAction SilentlyContinue
            foreach ($dir in $dirs) {
                # 检查多种 SSMS.exe 位置
                $exeCandidates = @(
                    (Join-Path $dir.FullName 'Release\Common7\IDE\SSMS.exe'),
                    (Join-Path $dir.FullName 'Common7\IDE\SSMS.exe')
                )
                foreach ($exe in $exeCandidates) {
                    if (Test-Path $exe) {
                        # 返回 SSMS 22 根目录（包含 Release 的那一级）
                        $root = Resolve-SSMSRootFromSSMSExe $exe
                        return @{
                            Path   = $root
                            Source = 'common_path'
                        }
                    }
                }
            }
        } catch {
            # 忽略单个驱动器的扫描错误，继续下一个
        }
    }
    return $null
}

# ==================== Tier 6: 进程探测 ====================

function Find-RunningProcess {
    $running = Get-Process -Name 'SSMS' -ErrorAction SilentlyContinue
    if (-not $running) { return $null }

    foreach ($proc in $running) {
        try {
            $exePath = $proc.MainModule.FileName
            $root = Resolve-SSMSRootFromSSMSExe $exePath
            if (Test-SSMSRoot $root) {
                return @{
                    Path   = $root
                    Source = 'running_process'
                }
            }
        } catch {
            # 进程可能没有权限访问 MainModule，继续下一个
            continue
        }
    }
    return $null
}

# ==================== 主检测流程 ====================

$detectionMap = @{
    'user_config'     = @{ Order = 1; Func = { Find-UserConfig } }
    'env'             = @{ Order = 2; Func = { Find-EnvVar } }
    'vswhere'         = @{ Order = 3; Func = { Find-VSWhere } }
    'registry'        = @{ Order = 4; Func = { Find-Registry } }
    'common_path'     = @{ Order = 5; Func = { Find-CommonPath } }
    'running_process' = @{ Order = 6; Func = { Find-RunningProcess } }
}

$result = $null

if ($Source -ne 'auto') {
    # 强制使用指定源
    Write-Log "[手动指定] 使用检测源: $Source"
    $tier = $detectionMap[$Source]
    $result = & $tier.Func
    if ($result) {
        Write-Log "✅ [$Source] 找到: $($result.Path)"
    } else {
        Write-Log "❌ [$Source] 未找到"
    }
} else {
    # 自动按 Tier 1-6 顺序探测
    foreach ($sourceName in ($detectionMap.Keys | Sort-Object { $detectionMap[$_].Order })) {
        $tier = $detectionMap[$sourceName]
        $tierNum = $tier.Order
        $result = & $tier.Func
        if ($result) {
            Write-TierLog -Tier $tierNum -Status 'OK' -Message "找到: $($result.Path)" -Color Green
            break
        } else {
            Write-TierLog -Tier $tierNum -Status 'SKIP' -Message "未发现，尝试下一级" -Color DarkGray
        }
    }
}

# ==================== 输出结果 ====================

if ($result) {
    $output = [PSCustomObject]@{
        Path       = $result.Path
        Source     = $result.Source
        Time       = (Get-Date -Format 'yyyy-MM-ddTHH:mm:ss')
        SSMSExe    = Join-Path $result.Path 'Common7\IDE\SSMS.exe'
    }

    # 持久化到用户配置（仅在 auto 模式 + 未禁用时）
    if ($Source -eq 'auto' -and -not $NoPersist) {
        try {
            $configDir = Join-Path $env:APPDATA 'SSMSBoostPlus'
            if (-not (Test-Path $configDir)) {
                New-Item -ItemType Directory -Path $configDir -Force | Out-Null
            }
            $persistData = @{
                ssms_install_path = $output.Path
                detection_time    = $output.Time
                detection_source  = $output.Source
            } | ConvertTo-Json
            $configFile = Join-Path $configDir 'config.json'
            Set-Content -Path $configFile -Value $persistData -Encoding UTF8 -Force
            Write-Log "[持久化] 已写入: $configFile"
        } catch {
            [Console]::Error.WriteLine("配置持久化失败: $_")
        }
    }

    if ($AsJson) {
        $output | ConvertTo-Json -Compress
    } elseif ($OutputFile) {
        # 写到 bat 指定的临时文件
        try {
            $parent = Split-Path $OutputFile -Parent
            if ($parent -and -not (Test-Path $parent)) {
                New-Item -ItemType Directory -Path $parent -Force | Out-Null
            }
            # 用无 BOM 的 UTF-8 写出（避免 bat 读取时被 U+FEFF 污染变量）
            $utf8NoBom = New-Object System.Text.UTF8Encoding($false)
            [System.IO.File]::WriteAllText($OutputFile, $output.Path, $utf8NoBom)
        } catch {
            [Console]::Error.WriteLine("写入输出文件失败: $_")
            # 失败时回退到 stdout，避免彻底无输出
            Write-Output $output.Path
        }
    } else {
        Write-Output $output.Path
    }
    exit 0
} else {
    # 所有 Tier 均失败
    $errorMsg = @"
[❌ 检测失败] 所有 6 级检测策略均未找到 SSMS 22 安装路径

建议排查步骤：
  1. 确认 SSMS 22 已正确安装（查找 SSMS.exe）
  2. 设置环境变量: `$env:SSMS22_PATH = 'D:\...\Release'
  3. 或运行: $PSCommandPath -Source vswhere
  4. 或使用 -Source 参数手动指定: registry / common_path / running_process

常见安装位置:
  - D:\Microsoft SQL Server Management Studio 22\Release\
  - C:\Program Files\Microsoft SQL Server Management Studio 22\
"@
    [Console]::Error.WriteLine($errorMsg)
    exit 1
}
