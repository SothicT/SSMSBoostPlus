using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SSMSBoostPlus.Installer;

/// <summary>
/// SSMSBoostPlus extension installer.
/// Finds SSMS installations, copies extension files, and triggers extension discovery.
/// Usage: InstallSSMSBoostPlus.exe           （双击直接在线下载并安装）
///        InstallSSMSBoostPlus.exe --download（在线下载并安装）
///        InstallSSMSBoostPlus.exe --install [--ssms22|--ssms19|--ssms18|--all]
///        InstallSSMSBoostPlus.exe --uninstall
/// </summary>
class Program
{
    /// <summary>远程仓库 URL（Release 分支）</summary>
    private const string RemoteRepoUrl = "http://172.16.10.168/AI%E9%A1%B9%E7%9B%AE%E9%9B%86%E5%90%88/SSMSBoostPlus/_git/SSMSBoostPlus";
    private const string RemoteBranch = "Release";

    // SSMS 可执行文件相对安装根目录的路径(候选目录需包含此文件才算命中)
    // 检测不再写死 SSMS 主版本号,版本标签由安装路径动态派生
    private const string SsmsExeRelativePath = @"Common7\IDE\Ssms.exe";

    private const string ExtensionFolderName = "SSMSBoostPlus";

    // Files that must be deployed to the SSMS extension directory
    // 此列表必须与 Release 分支 files/ 目录保持同步
    private static readonly string[] RequiredFiles = new[]
    {
        // ===== 核心 DLL（4个+4个PDB） =====
        "SSMSBoostPlus.SSMS.dll",
        "SSMSBoostPlus.SSMS.pdb",
        "SSMSBoostPlus.Core.dll",
        "SSMSBoostPlus.Core.pdb",
        "SSMSBoostPlus.AI.dll",
        "SSMSBoostPlus.AI.pdb",
        "SSMSBoostPlus.Shared.dll",
        "SSMSBoostPlus.Shared.pdb",

        // ===== 配置文件 =====
        "SSMSBoostPlus.SSMS.pkgdef",
        "Menus.ctmenu",
        "extension.vsixmanifest",

        // ===== JS 资源 =====
        "marked.min.js",
        "stream-engine.js",

        // ===== 第三方依赖 =====
        "DiffPlex.dll",
        "Serilog.dll",
        "Serilog.Sinks.File.dll",
        "Serilog.Enrichers.Environment.dll",
        "Microsoft.Data.SqlClient.dll",
        "Newtonsoft.Json.dll",
        "Microsoft.Bcl.AsyncInterfaces.dll",
        "Microsoft.Extensions.Configuration.dll",
        "Microsoft.Extensions.Configuration.Abstractions.dll",
        "Microsoft.Extensions.Configuration.Binder.dll",
        "Microsoft.Extensions.Configuration.FileExtensions.dll",
        "Microsoft.Extensions.Configuration.Json.dll",
        "Microsoft.Extensions.FileProviders.Abstractions.dll",
        "Microsoft.Extensions.FileProviders.Physical.dll",
        "Microsoft.Extensions.FileSystemGlobbing.dll",
        "Microsoft.Extensions.Primitives.dll",
        "Microsoft.SqlServer.TransactSql.ScriptDom.dll",
        "Markdig.dll",
        "Markdown.Xaml.dll",
        "Microsoft.Xaml.Behaviors.dll",
        "System.Memory.dll",
        "System.Runtime.CompilerServices.Unsafe.dll",
        "System.Text.Json.dll",
        "System.Threading.Tasks.Extensions.dll",
        "System.ValueTuple.dll",
        "System.Buffers.dll",
        "System.Numerics.Vectors.dll",

        // ===== WebView2 =====
        "Microsoft.Web.WebView2.Core.dll",
        "Microsoft.Web.WebView2.Wpf.dll",
        "WebView2Loader.dll",
    };

    static int Main(string[] args)
    {
        Console.WriteLine("========================================");
        Console.WriteLine("  SSMSBoostPlus Extension Installer");
        Console.WriteLine("========================================");
        Console.WriteLine();

        // ========== 在线下载模式（双击默认或带 --download） ==========
        bool isDownloadMode = args.Contains("--download") || args.Length == 0;

        if (isDownloadMode)
        {
            return DownloadAndInstall();
        }

        // ========== 本地安装模式 ==========
        var sourceDir = FindSourceDirectory();
        if (sourceDir == null)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: Could not find extension files.");
            Console.WriteLine();
            Console.WriteLine("请直接双击本程序在线下载并安装，或手动指定以下参数：");
            Console.WriteLine("  --download              在线下载 Release 分支并安装");
            Console.WriteLine("  --uninstall              卸载扩展");
            Console.WriteLine("  --ssms22 / --ssms19 / --all  指定 SSMS 版本");
            Console.ResetColor();
            return 1;
        }

        Console.WriteLine($"Source directory: {sourceDir}");
        Console.WriteLine();

        bool uninstall = args.Contains("--uninstall");
        bool installAll = args.Contains("--all");
        string? specificVersion = args.FirstOrDefault(a => a.StartsWith("--ssms"));
        if (specificVersion != null) specificVersion = specificVersion.Substring(2).ToUpper(); // e.g. "SSMS22"

        // Find SSMS installations
        var installations = FindSsmsInstallations();
        if (installations.Count == 0)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: No SSMS installations found on this system.");
            Console.ResetColor();
            return 1;
        }

        Console.WriteLine("Found SSMS installations:");
        foreach (var inst in installations)
        {
            Console.WriteLine($"  [{inst.Key}] {inst.Name} at {inst.InstallPath}");
        }
        Console.WriteLine();

        // Filter based on arguments
        var targets = new List<(string Key, string Name, string InstallPath)>();
        if (installAll)
        {
            targets = installations;
        }
        else if (specificVersion != null)
        {
            var match = installations.FirstOrDefault(i => i.Name.Replace(" ", "").ToUpper().Contains(specificVersion));
            if (match.Name != null)
                targets.Add(match);
            else
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"ERROR: SSMS version '{specificVersion}' not found.");
                Console.ResetColor();
                return 1;
            }
        }
        else
        {
            // Interactive: ask which to install
            if (installations.Count == 1)
            {
                targets.Add(installations[0]);
            }
            else
            {
                Console.WriteLine("Select SSMS version(s) to install to:");
                for (int i = 0; i < installations.Count; i++)
                {
                    Console.WriteLine($"  {i + 1}. {installations[i].Name}");
                }
                Console.WriteLine($"  A. All");
                Console.Write("Enter choice (1-A): ");
                var input = Console.ReadLine()?.Trim().ToUpper();
                if (input == "A")
                {
                    targets = installations;
                }
                else if (int.TryParse(input, out int choice) && choice >= 1 && choice <= installations.Count)
                {
                    targets.Add(installations[choice - 1]);
                }
                else
                {
                    Console.WriteLine("Invalid choice.");
                    return 1;
                }
            }
        }

        if (uninstall)
        {
            foreach (var target in targets)
            {
                UninstallFrom(target);
            }
        }
        else
        {
            foreach (var target in targets)
            {
                InstallTo(target, sourceDir);
            }
        }

        Console.WriteLine();
        Console.ForegroundColor = ConsoleColor.Green;
        Console.WriteLine(uninstall ? "Uninstall completed." : "Install completed.");
        Console.ResetColor();
        Console.WriteLine();
        Console.WriteLine("Please restart SSMS for changes to take effect.");

        try { Console.ReadKey(); } catch { /* non-interactive mode */ }

        return 0;
    }

    /// <summary>
    /// 在线下载模式：从远程仓库 Release 分支下载最新文件并安装。
    /// </summary>
    private static int DownloadAndInstall()
    {
        Console.WriteLine(">>> 在线下载模式：从 Release 分支获取最新版本...");
        Console.WriteLine();

        // 1. 检查 Git 是否可用
        if (!IsGitAvailable())
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine("ERROR: 未找到 Git！");
            Console.WriteLine("请先安装 Git: https://git-scm.com");
            Console.ResetColor();
            try { Console.ReadKey(); } catch { }
            return 1;
        }

        // 2. 关闭正在运行的 SSMS 进程（避免 DLL 锁定）
        Console.WriteLine("正在检查 SSMS 进程...");
        KillSsmsProcesses();

        // 3. 创建临时目录
        var tempDir = Path.Combine(Path.GetTempPath(), "SSMSBoostPlus_Installer_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        Console.WriteLine($"临时目录: {tempDir}");
        Console.WriteLine();

        try
        {
            // 4. 使用 git clone --depth 1 下载 Release 分支
            Console.WriteLine("正在从仓库下载最新文件（Release 分支）...");
            Console.WriteLine($"  仓库: {RemoteRepoUrl}");
            Console.WriteLine();

            var cloneDir = Path.Combine(tempDir, "repo");
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = $"clone --depth 1 --branch {RemoteBranch} \"{RemoteRepoUrl}\" \"{cloneDir}\"",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };
            process.Start();
            string output = process.StandardOutput.ReadToEnd();
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();

            if (process.ExitCode != 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine($"下载失败 (exit code: {process.ExitCode})");
                if (!string.IsNullOrEmpty(error))
                    Console.WriteLine($"错误信息: {error}");
                Console.ResetColor();
                try { Console.ReadKey(); } catch { }
                return 1;
            }

            Console.WriteLine("下载完成！");
            Console.WriteLine();

            // 获取克隆仓库的当前版本 hash
            var versionHash = GetGitHeadHash(cloneDir);
            Console.WriteLine($"Release 版本: {versionHash?.Substring(0, 8) ?? "未知"}");
            Console.WriteLine();

            // 5. 查找扩展文件目录（先看根目录有没有 InstalledPackage/files，再看 src/SSMS/bin/Release 等）
            string? sourceDir = FindFilesInClone(cloneDir);
            if (sourceDir == null)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("ERROR: 下载的文件中未找到扩展文件。");
                Console.ResetColor();
                try { Console.ReadKey(); } catch { }
                return 1;
            }

            Console.WriteLine($"扩展文件目录: {sourceDir}");
            Console.WriteLine();

            // 6. 查找 SSMS 安装
            var installations = FindSsmsInstallations();
            if (installations.Count == 0)
            {
                Console.ForegroundColor = ConsoleColor.Red;
                Console.WriteLine("ERROR: 未找到任何 SSMS 安装。");
                Console.ResetColor();
                try { Console.ReadKey(); } catch { }
                return 1;
            }

            Console.WriteLine("找到以下 SSMS 安装：");
            foreach (var inst in installations)
            {
                Console.WriteLine($"  [{inst.Key}] {inst.Name}  -  {inst.InstallPath}");
            }
            Console.WriteLine();

            // 7. 自动安装到所有找到的 SSMS
            Console.WriteLine("正在安装到所有 SSMS 实例...");
            foreach (var target in installations)
            {
                InstallTo(target, sourceDir);
                // 安装完成后写入版本信息文件，避免 SSMS 重启后提示"新版本"
                if (!string.IsNullOrEmpty(versionHash))
                    WriteVersionInfo(target, versionHash);
            }

            Console.WriteLine();
            Console.ForegroundColor = ConsoleColor.Green;
            Console.WriteLine("========================================");
            Console.WriteLine("  安装完成！");
            Console.WriteLine("========================================");
            Console.ResetColor();
            Console.WriteLine();
            Console.WriteLine("请重启 SSMS 以加载扩展。");
            Console.WriteLine();

            try { Console.ReadKey(); } catch { }

            return 0;
        }
        finally
        {
            // 清理临时目录
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch { /* ignore cleanup errors */ }
        }
    }

    /// <summary>
    /// 获取克隆仓库的 HEAD commit hash。
    /// </summary>
    private static string? GetGitHeadHash(string repoDir)
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "log -1 --format=%H",
                    WorkingDirectory = repoDir,
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(5000);
            return output?.Trim();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 将版本信息写入 SSMS 扩展目录的 version_info.json，
    /// 避免首次安装后 SSMS 检查更新时显示"新版本"。
    /// </summary>
    private static void WriteVersionInfo(
        (string Key, string Name, string InstallPath) target, string versionHash)
    {
        try
        {
            var extDir = Path.Combine(target.InstallPath, @"Common7\IDE\Extensions", ExtensionFolderName);
            var versionFile = Path.Combine(extDir, "version_info.json");
            var info = $"{{\"localHash\":\"{versionHash}\",\"lastCheckTime\":\"{DateTime.Now:O}\"}}";
            File.WriteAllText(versionFile, info);
            Console.WriteLine($"  [OK] 版本信息已写入: {versionHash.Substring(0, 8)}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [WARNING] 写入版本信息失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 检查 Git 是否已安装且可用。
    /// </summary>
    private static bool IsGitAvailable()
    {
        try
        {
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "git",
                    Arguments = "--version",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };
            process.Start();
            process.WaitForExit(3000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// 关闭所有正在运行的 SSMS 进程。
    /// </summary>
    private static void KillSsmsProcesses()
    {
        try
        {
            var processes = Process.GetProcessesByName("Ssms");
            if (processes.Length > 0)
            {
                Console.WriteLine($"发现 {processes.Length} 个 SSMS 进程正在运行，正在关闭...");
                foreach (var proc in processes)
                {
                    try
                    {
                        proc.CloseMainWindow();
                        if (!proc.WaitForExit(5000))
                        {
                            proc.Kill();
                            proc.WaitForExit(3000);
                        }
                        Console.WriteLine($"  [OK] 已关闭 SSMS 进程 (PID: {proc.Id})");
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"  [WARNING] 关闭 SSMS 进程失败: {ex.Message}");
                    }
                }
                Console.WriteLine();
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine($"  [WARNING] 检查 SSMS 进程时出错: {ex.Message}");
            Console.WriteLine();
        }
    }

    /// <summary>
    /// 在克隆的仓库目录中查找扩展文件。
    /// </summary>
    private static string? FindFilesInClone(string cloneDir)
    {
        // ⚠ Installer/InstallerPackage/files/ 路径已废弃（旧发布结构），不再使用
        // 唯一正确的目录是根目录下的 files/

        // 检查根目录下的 files/ 目录（Release 分支最新的部署文件）
        var rootFiles = Path.Combine(cloneDir, "files");
        if (Directory.Exists(rootFiles) && File.Exists(Path.Combine(rootFiles, "SSMSBoostPlus.SSMS.dll")))
            return rootFiles;

        // 检查 src/SSMS/bin/Release/net472（编译输出目录）
        var buildOut = Path.Combine(cloneDir, "src", "SSMS", "bin", "Release", "net472");
        if (Directory.Exists(buildOut) && File.Exists(Path.Combine(buildOut, "SSMSBoostPlus.SSMS.dll")))
            return buildOut;

        // 检查根目录（EXE 所在目录）
        if (File.Exists(Path.Combine(cloneDir, "SSMSBoostPlus.SSMS.dll")))
            return cloneDir;

        return null;
    }

    private static string? FindSourceDirectory()
    {
        // Look for extension files relative to the EXE location
        var exeDir = AppDomain.CurrentDomain.BaseDirectory;

        // Check: EXE is in the same folder as the DLLs
        if (File.Exists(Path.Combine(exeDir, "SSMSBoostPlus.SSMS.dll")))
            return exeDir;

        // Check: EXE is one level up from Extensions/ subfolder
        var extDir = Path.Combine(exeDir, "Extensions");
        if (Directory.Exists(extDir) && File.Exists(Path.Combine(extDir, "SSMSBoostPlus.SSMS.dll")))
            return extDir;

        return null;
    }

    /// <summary>
    /// 查找本机所有 SSMS 安装。
    /// 检测策略(版本无关、位置无关):
    ///   1. 主策略:vswhere.exe 枚举所有 SSMS/VS 实例(覆盖任意版本、任意安装位置)
    ///   2. 回退1:枚举 HKLM\SOFTWARE\Microsoft\SSMS 下全部数字开头子键
    ///           (含 22.0_xxx / 21.0_xxx / 14.0 等),读 SSMSInstallRoot
    ///   3. 回退2:Uninstall 节点中 DisplayName 含 "SQL Server Management Studio" 的 InstallLocation
    /// 不再假设安装目录在 Program Files,也不再写死 SSMS 主版本号。
    /// </summary>
    private static List<(string Key, string Name, string InstallPath)> FindSsmsInstallations()
    {
        var results = new List<(string Key, string Name, string InstallPath)>();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // ===== 主策略:vswhere 枚举实例(覆盖任意版本、任意安装位置) =====
        foreach (var path in FindViaVsWhere())
            candidates.Add(path);

        // ===== 回退1:枚举 SSMS 注册表下全部实例子键(不再写死 22.0 精确键) =====
        foreach (var baseKey in new[]
        {
            @"SOFTWARE\Microsoft\SSMS",
            @"SOFTWARE\WOW6432Node\Microsoft\SSMS",
        })
        {
            using var key = Registry.LocalMachine.OpenSubKey(baseKey);
            if (key == null) continue;
            foreach (var subName in key.GetSubKeyNames())
            {
                if (subName.Length == 0 || !char.IsDigit(subName[0])) continue;
                using var sub = key.OpenSubKey(subName);
                var root = sub?.GetValue("SSMSInstallRoot") as string;
                if (root is null) continue;
                candidates.Add(root);
            }
        }

        // ===== 回退2+4:Uninstall 节点,InstallLocation + DisplayIcon 双路反推 =====
        foreach (var uninstallKey in new[]
        {
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall",
            @"SOFTWARE\WOW6432Node\Microsoft\Windows\CurrentVersion\Uninstall",
        })
        {
            using var key = Registry.LocalMachine.OpenSubKey(uninstallKey);
            if (key == null) continue;
            foreach (var subName in key.GetSubKeyNames())
            {
                using var sub = key.OpenSubKey(subName);
                if (sub == null) continue;
                var displayName = sub.GetValue("DisplayName") as string;
                if (displayName is null) continue;
                if (displayName.IndexOf("SQL Server Management Studio", StringComparison.OrdinalIgnoreCase) < 0) continue;

                // 回退2:InstallLocation 直接反推
                var installLocation = sub.GetValue("InstallLocation") as string;
                if (!string.IsNullOrEmpty(installLocation))
                {
                    candidates.Add(installLocation!);
                    continue;
                }

                // 回退4:DisplayIcon 反推目录(填补 InstallLocation 为空的盲区)
                var displayIcon = sub.GetValue("DisplayIcon") as string;
                if (!string.IsNullOrEmpty(displayIcon) &&
                    displayIcon!.IndexOf("Ssms.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var iconDir = Path.GetDirectoryName(displayIcon);
                    if (!string.IsNullOrEmpty(iconDir))
                    {
                        // DisplayIcon 通常指向 {root}\Common7\IDE\Ssms.exe,上溯两级得根
                        if (iconDir.EndsWith(@"Common7\IDE", StringComparison.OrdinalIgnoreCase))
                            candidates.Add(Path.GetFullPath(Path.Combine(iconDir, "..", "..")));
                        else
                            candidates.Add(iconDir);
                    }
                }
            }
        }

        // ===== 回退3:HKCU IsoShell_Config InstallDir(老版本 SSMS 自定义安装路径) =====
        // 老版本 SSMS(17/18/19 等独立 MSI 安装)在当前用户配置下写入
        // "HKCU\Software\Microsoft\SQL Server Management Studio\{version}_IsoShell_Config",
        // 其 InstallDir 值记录真实安装根目录,覆盖自定义安装路径场景。
        using var hkcuKey = Registry.CurrentUser.OpenSubKey(
            @"Software\Microsoft\SQL Server Management Studio");
        if (hkcuKey != null)
        {
            foreach (var subName in hkcuKey.GetSubKeyNames())
            {
                if (!subName.EndsWith("_IsoShell_Config", StringComparison.OrdinalIgnoreCase))
                    continue;
                using var sub = hkcuKey.OpenSubKey(subName);
                var installDir = sub?.GetValue("InstallDir") as string;
                if (!string.IsNullOrEmpty(installDir))
                    candidates.Add(installDir!);
            }
        }

        // ===== 命中判定:候选目录(或其 Release 子目录)必须存在 Ssms.exe =====
        foreach (var candidate in candidates)
        {
            var ssmsRoot = ResolveSsmsInstallDir(candidate);
            if (ssmsRoot != null && found.Add(ssmsRoot))
            {
                var (keyName, displayName) = DeriveVersionLabel(ssmsRoot);
                results.Add((keyName, displayName, ssmsRoot));
            }
        }

        return results;
    }

    /// <summary>
    /// 通过 vswhere.exe 枚举所有已安装的 SSMS/VS 实例安装路径。
    /// vswhere 由 SSMS 22(VS Installer)随附,能返回任意位置、任意版本的实例路径;
    /// 旧版独立 MSI 安装的 SSMS 若未带 VS Installer 则静默回退到注册表策略。
    /// </summary>
    private static List<string> FindViaVsWhere()
    {
        var result = new List<string>();
        var vswhere = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            @"Microsoft Visual Studio\Installer\vswhere.exe");
        if (!File.Exists(vswhere))
            return result;

        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = vswhere,
                    Arguments = "-prerelease -products * -property installationPath",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                }
            };
            process.Start();
            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(10000);
            foreach (var line in output.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var path = line.Trim();
                if (!string.IsNullOrEmpty(path)) result.Add(path);
            }
        }
        catch
        {
            // vswhere 不可用,静默回退到注册表策略
        }

        return result;
    }

    /// <summary>
    /// 将候选目录解析为包含 Common7\IDE\Ssms.exe 的安装根目录。
    /// 兼容候选目录直接含 Common7,或其下还有 Release 子目录两种情况。
    /// </summary>
    private static string? ResolveSsmsInstallDir(string candidate)
    {
        if (File.Exists(Path.Combine(candidate, SsmsExeRelativePath)))
            return candidate;
        var withRelease = Path.Combine(candidate, "Release");
        if (File.Exists(Path.Combine(withRelease, SsmsExeRelativePath)))
            return withRelease;
        return null;
    }

    /// <summary>
    /// 从安装路径派生版本标签,例如 "...\Microsoft SQL Server Management Studio 22\Release" → ("SSMS22", "SSMS 22")。
    /// 版本无关:不依赖写死的版本列表,未来新版本也会自动识别。
    /// </summary>
    private static (string Key, string Name) DeriveVersionLabel(string installPath)
    {
        var match = Regex.Match(installPath, @"Management Studio (\d+)");
        if (match.Success)
        {
            var ver = match.Groups[1].Value;
            return ($"SSMS{ver}", $"SSMS {ver}");
        }
        return ("SSMS", "SSMS");
    }

    private static void InstallTo((string Key, string Name, string InstallPath) target, string sourceDir)
    {
        var extensionsBase = Path.Combine(target.InstallPath, @"Common7\IDE\Extensions");
        var extDir = Path.Combine(extensionsBase, ExtensionFolderName);

        Console.WriteLine($"Installing to {target.Name}...");
        Console.WriteLine($"  Target: {extDir}");

        // Create directory if not exists
        Directory.CreateDirectory(extDir);

        // ========== 复制 files/ 目录下所有文件（覆盖更新） ==========
        var allFiles = Directory.GetFiles(sourceDir);
        int copied = 0, skipped = 0;

        foreach (var srcPath in allFiles)
        {
            var fileName = Path.GetFileName(srcPath);
            var dstPath = Path.Combine(extDir, fileName);

            try
            {
                File.Copy(srcPath, dstPath, overwrite: true);
                copied++;
            }
            catch (Exception ex)
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  WARNING: Failed to copy {fileName}: {ex.Message}");
                Console.ResetColor();
                skipped++;
            }
        }

        // ========== 验证核心文件是否存在（仅警告，不阻止安装） ==========
        int missing = 0;
        foreach (var file in RequiredFiles)
        {
            if (!File.Exists(Path.Combine(extDir, file)))
            {
                Console.ForegroundColor = ConsoleColor.Yellow;
                Console.WriteLine($"  WARNING: Core file missing after copy: {file}");
                Console.ResetColor();
                missing++;
            }
        }

        Console.WriteLine($"  Copied {copied} files, {skipped} skipped, {missing} core files missing.");

        // Trigger extension discovery
        var configFile = Path.Combine(extensionsBase, "extensions.configurationchanged");
        File.WriteAllText(configFile, DateTime.UtcNow.Ticks.ToString());
        Console.WriteLine("  Triggered extension discovery.");
    }

    private static void UninstallFrom((string Key, string Name, string InstallPath) target)
    {
        var extDir = Path.Combine(target.InstallPath, @"Common7\IDE\Extensions", ExtensionFolderName);

        Console.WriteLine($"Uninstalling from {target.Name}...");
        Console.WriteLine($"  Target: {extDir}");

        if (Directory.Exists(extDir))
        {
            Directory.Delete(extDir, recursive: true);
            Console.WriteLine("  Removed extension directory.");

            // Trigger extension discovery
            var extensionsBase = Path.Combine(target.InstallPath, @"Common7\IDE\Extensions");
            var configFile = Path.Combine(extensionsBase, "extensions.configurationchanged");
            File.WriteAllText(configFile, DateTime.UtcNow.Ticks.ToString());
            Console.WriteLine("  Triggered extension discovery.");
        }
        else
        {
            Console.WriteLine("  Extension not found (already uninstalled).");
        }
    }
}
