using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;
using Microsoft.Win32;

namespace SSMSBoostPlus.Uninstaller;

/// <summary>
/// SSMSBoostPlus extension uninstaller.
/// Interactive tool that finds SSMS installations with the extension and removes it.
/// </summary>
class Program
{
    private const string SsmsExeRelativePath = @"Common7\IDE\Ssms.exe";
    private const string ExtensionFolderName = "SSMSBoostPlus";

    private static readonly string[] RequiredFiles =
    {
        "SSMSBoostPlus.SSMS.dll",
        "SSMSBoostPlus.Core.dll",
        "SSMSBoostPlus.AI.dll",
        "SSMSBoostPlus.Shared.dll",
    };

    static int Main(string[] args)
    {
        bool silentAll = args.Length > 0 &&
            (string.Equals(args[0], "--all", StringComparison.OrdinalIgnoreCase) ||
             string.Equals(args[0], "-a", StringComparison.OrdinalIgnoreCase));

        Console.WriteLine("============================================");
        Console.WriteLine("  SSMSBoostPlus - 扩展卸载程序");
        Console.WriteLine("============================================");
        Console.WriteLine();

        // Step 1: Kill SSMS processes
        KillSsmsProcesses();

        // Step 2: Find all SSMS installations
        Console.WriteLine("正在扫描已安装的 SSMS...");
        var installations = FindSsmsInstallations();

        if (installations.Count == 0)
        {
            Console.WriteLine("未找到任何 SSMS 安装。");
            WaitForExit();
            return 0;
        }

        // Step 3: Filter to only those with extension installed
        var installed = new List<(string Key, string Name, string InstallPath)>();
        foreach (var inst in installations)
        {
            var extDir = Path.Combine(inst.InstallPath, @"Common7\IDE\Extensions", ExtensionFolderName);
            if (Directory.Exists(extDir))
                installed.Add(inst);
        }

        if (installed.Count == 0)
        {
            Console.WriteLine("未在任何 SSMS 安装中找到 SSMSBoostPlus 扩展，无需卸载。");
            WaitForExit();
            return 0;
        }

        Console.WriteLine($"发现 {installed.Count} 个已安装 SSMSBoostPlus 的 SSMS 实例：");
        Console.WriteLine();

        // Step 4: Display list
        for (int i = 0; i < installed.Count; i++)
        {
            var extDir = Path.Combine(installed[i].InstallPath, @"Common7\IDE\Extensions", ExtensionFolderName);
            var fileCount = Directory.Exists(extDir) ? Directory.GetFiles(extDir).Length : 0;
            Console.WriteLine($"  [{i + 1}] {installed[i].Name}");
            Console.WriteLine($"      路径: {installed[i].InstallPath}");
            Console.WriteLine($"      文件: {fileCount} 个");
        }

        // Step 5: Silent --all mode
        if (silentAll)
        {
            Console.WriteLine();
            Console.WriteLine("检测到 --all 参数，正在卸载全部版本...");
            int success = 0, fail = 0;
            foreach (var inst in installed)
            {
                if (UninstallFrom(inst))
                    success++;
                else
                    fail++;
            }
            Console.WriteLine();
            Console.WriteLine($"卸载完成: {success} 个成功, {fail} 个失败。");
            return fail > 0 ? 1 : 0;
        }

        // Step 6: Interactive menu
        Console.WriteLine();
        Console.WriteLine("  [A]  卸载全部版本");
        Console.WriteLine("  [Q]  退出");
        Console.WriteLine();
        Console.Write("请选择要卸载的版本 (输入编号 / A / Q): ");

        var input = Console.ReadLine()?.Trim().ToUpperInvariant() ?? "";

        if (input == "Q" || input == "")
        {
            Console.WriteLine("已取消。");
            return 0;
        }

        if (input == "A")
        {
            Console.WriteLine();
            Console.WriteLine("正在卸载全部版本...");
            foreach (var inst in installed)
                UninstallFrom(inst);
        }
        else if (int.TryParse(input, out int index) && index >= 1 && index <= installed.Count)
        {
            var target = installed[index - 1];
            Console.WriteLine();
            UninstallFrom(target);
        }
        else
        {
            Console.ForegroundColor = ConsoleColor.Yellow;
            Console.WriteLine("无效选择，未执行任何卸载操作。");
            Console.ResetColor();
        }

        Console.WriteLine();
        Console.WriteLine("按任意键退出...");
        Console.ReadKey();
        return 0;
    }

    // =====================================================================
    // SSMS 安装检测逻辑 (与安装程序相同)
    // =====================================================================

    private static List<(string Key, string Name, string InstallPath)> FindSsmsInstallations()
    {
        var results = new List<(string Key, string Name, string InstallPath)>();
        var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var candidates = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 主策略: vswhere
        foreach (var path in FindViaVsWhere())
            candidates.Add(path);

        // 回退1: SSMS 注册表子键
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

        // 回退2+4: Uninstall 节点双路反推
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

                var installLocation = sub.GetValue("InstallLocation") as string;
                if (!string.IsNullOrEmpty(installLocation))
                {
                    candidates.Add(installLocation!);
                    continue;
                }

                var displayIcon = sub.GetValue("DisplayIcon") as string;
                if (!string.IsNullOrEmpty(displayIcon) &&
                    displayIcon!.IndexOf("Ssms.exe", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var iconDir = Path.GetDirectoryName(displayIcon);
                    if (!string.IsNullOrEmpty(iconDir))
                    {
                        if (iconDir.EndsWith(@"Common7\IDE", StringComparison.OrdinalIgnoreCase))
                            candidates.Add(Path.GetFullPath(Path.Combine(iconDir, "..", "..")));
                        else
                            candidates.Add(iconDir);
                    }
                }
            }
        }

        // 回退3: HKCU IsoShell_Config InstallDir
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

        // 命中判定
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
            // 静默回退
        }

        return result;
    }

    private static string? ResolveSsmsInstallDir(string candidate)
    {
        if (File.Exists(Path.Combine(candidate, SsmsExeRelativePath)))
            return candidate;
        var withRelease = Path.Combine(candidate, "Release");
        if (File.Exists(Path.Combine(withRelease, SsmsExeRelativePath)))
            return withRelease;
        return null;
    }

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

    // =====================================================================
    // 卸载逻辑
    // =====================================================================

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
            Console.WriteLine($"  [WARNING] 枚举 SSMS 进程失败: {ex.Message}");
        }
    }

    /// <summary>
    /// 从指定 SSMS 安装中卸载扩展。
    /// </summary>
    /// <returns>true 表示卸载成功或无需卸载，false 表示遇到错误。</returns>
    private static bool UninstallFrom((string Key, string Name, string InstallPath) target)
    {
        var extDir = Path.Combine(target.InstallPath, @"Common7\IDE\Extensions", ExtensionFolderName);

        Console.WriteLine($"├─ 正在卸载: {target.Name}");
        Console.WriteLine($"│  路径: {extDir}");

        if (!Directory.Exists(extDir))
        {
            Console.WriteLine($"│  [SKIP] 扩展不存在 (可能已卸载)");
            Console.WriteLine($"└─");
            return true;
        }

        try
        {
            // 记录文件数用于反馈
            var fileCount = Directory.GetFiles(extDir).Length;

            Directory.Delete(extDir, recursive: true);
            Console.WriteLine($"│  [OK] 已删除扩展目录 ({fileCount} 个文件)");

            // 触发 SSMS 扩展发现
            var extensionsBase = Path.Combine(target.InstallPath, @"Common7\IDE\Extensions");
            var configFile = Path.Combine(extensionsBase, "extensions.configurationchanged");
            File.WriteAllText(configFile, DateTime.UtcNow.Ticks.ToString());
            Console.WriteLine($"│  [OK] 已触发扩展发现");

            Console.WriteLine($"└─ 卸载完成");
            return true;
        }
        catch (Exception ex)
        {
            Console.ForegroundColor = ConsoleColor.Red;
            Console.WriteLine($"│  [FAIL] 卸载失败: {ex.Message}");
            Console.ResetColor();
            Console.WriteLine($"└─");
            return false;
        }
    }

    private static void WaitForExit()
    {
        Console.WriteLine();
        Console.WriteLine("按任意键退出...");
        Console.ReadKey();
    }
}
