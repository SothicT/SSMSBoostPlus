using System;
using System.Collections.Generic;
using System.ComponentModel.Design;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using SSMSBoostPlus.SSMS.UI.Dialogs;

namespace SSMSBoostPlus.SSMS.Commands;

/// <summary>
/// 版本检查命令 — 检查 Release 分支是否有新版本。
/// VSCT: cmdidVersionCheck = 0x0302
/// </summary>
public sealed class VersionCheckCommand : BaseCommand<VersionCheckCommand>
{
    private readonly VersionCheckService _versionService;
    private readonly Action<string> _logAction;
    private readonly AsyncPackage _package;

    private VersionCheckCommand(AsyncPackage package, OleMenuCommandService commandService,
        VersionCheckService versionService, Action<string> logAction)
        : base(package, commandService, 0x0302)
    {
        _versionService = versionService;
        _logAction = logAction;
        _package = package;
    }

    public static async Task InitializeAsync(AsyncPackage package, VersionCheckService versionService,
        Action<string> logAction)
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
        var commandService = (OleMenuCommandService?)await package.GetServiceAsync(typeof(IMenuCommandService));
        Instance = new VersionCheckCommand(package, commandService!, versionService, logAction);
    }

    protected override void Execute(object sender, EventArgs e)
    {
        // ★ 即时状态栏反馈：点击后立即显示"正在检查更新..."，不等待网络操作
        SetStatusText("SSMSBoostPlus: 正在检查更新...");

        // 使用 RunAsync 而非 Run，避免阻塞 SSMS UI 线程
        _ = CheckForUpdateAsync(isManual: true);
    }

    /// <summary>
    /// 设置 SSMS 状态栏文字。
    /// </summary>
    private void SetStatusText(string? text)
    {
        try
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            var statusBar = (IVsStatusbar?)((System.IServiceProvider)_package).GetService(typeof(SVsStatusbar));
            if (statusBar != null)
            {
                if (text != null)
                    statusBar.SetText(text);
                else
                    statusBar.Clear();
            }
        }
        catch
        {
            // Status bar is best-effort
        }
    }

    /// <summary>
    /// 执行版本检查核心逻辑（手动检查和自动检查共用）。
    /// </summary>
    public async Task CheckForUpdateAsync(bool isManual)
    {
        // 手动检查时使用带超时的 CancellationToken，避免用户长时间等待无响应
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        try
        {
            Log($"[VersionCheck] 开始检查更新 ({(isManual ? "手动" : "自动")})");

            var localHash = _versionService.GetLocalVersionHash();

            // 网络操作使用 Task.Run + CancellationToken，确保不阻塞 UI 线程
            // 超时 15 秒（手动检查）/ 30 秒（自动检查）
            var timeoutMs = isManual ? 15000 : 30000;
            using var timeoutCts = new CancellationTokenSource(timeoutMs);

            string remoteHash;
            try
            {
                remoteHash = await Task.Run(
                    () => _versionService.GetLatestReleaseHashAsync().GetAwaiter().GetResult(),
                    timeoutCts.Token);
            }
            catch (OperationCanceledException)
            {
                if (isManual)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    SetStatusText(null);
                    System.Windows.MessageBox.Show(
                        "检查更新超时，请检查网络连接后重试。",
                        "SSMSBoostPlus - 检查更新",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }
                _versionService.UpdateLastCheckTime();
                return;
            }

            // 检查完成，清除状态栏
            if (isManual)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                SetStatusText(null);
            }

            if (string.IsNullOrEmpty(remoteHash))
            {
                if (isManual)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    System.Windows.MessageBox.Show(
                        "无法连接到代码仓库，请检查网络连接。",
                        "SSMSBoostPlus - 检查更新",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Warning);
                }
                _versionService.UpdateLastCheckTime();
                return;
            }

            if (localHash == remoteHash)
            {
                if (isManual)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    System.Windows.MessageBox.Show(
                        "您已安装最新版本，无需更新。",
                        "SSMSBoostPlus - 检查更新",
                        System.Windows.MessageBoxButton.OK,
                        System.Windows.MessageBoxImage.Information);
                }
                _versionService.UpdateLastCheckTime();
                return;
            }

            var commits = new List<string>();
            if (!string.IsNullOrEmpty(localHash))
            {
                commits = await _versionService.GetCommitsSinceAsync(localHash, remoteHash);
            }
            else
            {
                commits.Add($"新版本: {remoteHash.Substring(0, 8)}");
            }

            Log($"[VersionCheck] 发现新版本，差异 commit 数: {commits.Count}");
            _versionService.UpdateLastCheckTime();

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (isManual)
            {
                ShowManualUpdateDialog(remoteHash, commits);
            }
            else
            {
                ShowAutoUpdateNotification(remoteHash, commits);
            }
        }
        catch (Exception ex)
        {
            Log($"[VersionCheck] 检查更新失败: {ex.Message}");
            if (isManual)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                SetStatusText(null);
                System.Windows.MessageBox.Show(
                    $"检查更新失败: {ex.Message}",
                    "SSMSBoostPlus - 检查更新",
                    System.Windows.MessageBoxButton.OK,
                    System.Windows.MessageBoxImage.Error);
            }
        }
    }

    private void ShowManualUpdateDialog(string remoteHash, List<string> commits)
    {
        var localHash = _versionService.GetLocalVersionHash();

        // 显示带 commit 列表的可滚动详情窗口（非模态，不阻塞 SSMS 操作）
        var detailDialog = new CommitDetailDialog(localHash, remoteHash, commits)
        {
            Title = "SSMSBoostPlus - 发现新版本",
            Width = 420,
            Height = 360
        };

        // 在详情窗口底部添加"立即更新"和"取消"按钮
        var updateButton = new System.Windows.Controls.Button
        {
            Content = "立即更新",
            Width = 90,
            Height = 28,
            FontSize = 12,
            FontWeight = FontWeights.Bold,
            Foreground = System.Windows.Media.Brushes.White,
            Background = new System.Windows.Media.SolidColorBrush(Color.FromRgb(0x2E, 0x75, 0xB6)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(Color.FromRgb(0x2E, 0x75, 0xB6)),
            Cursor = System.Windows.Input.Cursors.Hand,
            Margin = new System.Windows.Thickness(0, 0, 6, 0)
        };
        var capturedRemoteHash = remoteHash; // 闭包捕获
        updateButton.Click += (s, e) =>
        {
            detailDialog.Close();
            _ = PerformUpdateAsync(capturedRemoteHash);
        };

        var cancelButton = new System.Windows.Controls.Button
        {
            Content = "取消",
            Width = 80,
            Height = 28,
            FontSize = 12,
            Background = new System.Windows.Media.SolidColorBrush(Color.FromRgb(0xF0, 0xF0, 0xF0)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(Color.FromRgb(0xCC, 0xCC, 0xCC)),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        cancelButton.Click += (s, e) => detailDialog.Close();

        var buttonPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new System.Windows.Thickness(0, 8, 0, 0)
        };
        buttonPanel.Children.Add(updateButton);
        buttonPanel.Children.Add(cancelButton);

        // 将按钮添加到详情窗口的 Grid 中（Row 4）
        var grid = (System.Windows.Controls.Grid)detailDialog.Content;
        grid.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto });
        grid.Children.Add(buttonPanel);
        System.Windows.Controls.Grid.SetRow(buttonPanel, 4);

        // 使用非模态 Show()，不阻塞 SSMS 操作
        detailDialog.Show();
    }

    private void ShowAutoUpdateNotification(string remoteHash, List<string> commits)
    {
        var localHash = _versionService.GetLocalVersionHash();
        var dialog = new VersionUpdateDialog(localHash, remoteHash, commits);

        var capturedRemoteHash = remoteHash;
        dialog.UpdateClicked += (s, e) =>
        {
            dialog.Close();
            _ = PerformUpdateAsync(capturedRemoteHash);
        };

        dialog.Show();
    }

    private void ShowCommitDetails(string? localHash, string remoteHash, List<string> commits)
    {
        var dialog = new CommitDetailDialog(localHash, remoteHash, commits)
        {
            Width = 420,
            Height = 360
        };
        dialog.Show();
    }

    /// <summary>
    /// 从 Release 分支下载安装程序并执行更新。
    /// </summary>
    private async Task PerformUpdateAsync(string remoteHash)
    {
        var confirmResult = System.Windows.MessageBox.Show(
            "更新过程将自动关闭 SSMS，并下载最新版本进行安装。\n\n" +
            "请确保当前工作已保存。\n\n是否立即更新？",
            "SSMSBoostPlus - 确认更新",
            System.Windows.MessageBoxButton.YesNo, System.Windows.MessageBoxImage.Warning);

        if (confirmResult != System.Windows.MessageBoxResult.Yes)
            return;

        try
        {
            Log($"[VersionCheck] 开始从 Release 分支下载安装程序...");

            // 0. 保存远程版本号到本地，避免重启后重复提示更新
            if (!string.IsNullOrEmpty(remoteHash))
            {
                _versionService.StoreVersion(remoteHash);
                Log($"[VersionCheck] 已保存版本号: {remoteHash.Substring(0, 8)}");
            }

            // 1. 检查 Git 是否可用
            if (!IsGitAvailable())
            {
                System.Windows.MessageBox.Show(
                    "未找到 Git，请先安装 Git (https://git-scm.com)。",
                    "SSMSBoostPlus - 更新失败",
                    System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                return;
            }

            // 2. 创建临时目录
            var tempDir = Path.Combine(Path.GetTempPath(), "SSMSBoostPlus_Update_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            var cloneDir = Path.Combine(tempDir, "repo");

            try
            {
                // 3. 使用 git clone --depth 1 下载 Release 分支
                Log($"[VersionCheck] 克隆仓库到临时目录: {cloneDir}");

                var cloneProcess = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = "git",
                        Arguments = $"clone --depth 1 --branch Release \"{VersionCheckService.GetRepoUrl()}\" \"{cloneDir}\"",
                        UseShellExecute = false,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8,
                        StandardErrorEncoding = System.Text.Encoding.UTF8,
                    }
                };
                cloneProcess.Start();
                var cloneOutput = await cloneProcess.StandardOutput.ReadToEndAsync();
                var cloneError = await cloneProcess.StandardError.ReadToEndAsync();
                cloneProcess.WaitForExit();

                if (cloneProcess.ExitCode != 0)
                {
                    Log($"[VersionCheck] 下载失败: {cloneError.Trim()}");
                    System.Windows.MessageBox.Show(
                        $"下载安装程序失败:\n{cloneError.Trim()}",
                        "SSMSBoostPlus - 更新失败",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                // 4. 查找 InstallSSMSBoostPlus.exe
                var installerPath = Path.Combine(cloneDir, "InstallSSMSBoostPlus.exe");
                if (!File.Exists(installerPath))
                {
                    Log("[VersionCheck] 下载的仓库中未找到 InstallSSMSBoostPlus.exe");
                    System.Windows.MessageBox.Show(
                        "下载的更新包中未找到安装程序，请稍后重试。",
                        "SSMSBoostPlus - 更新失败",
                        System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
                    return;
                }

                // 5. 复制到更易访问的临时位置（避免 git 清理时丢失）
                var targetExe = Path.Combine(tempDir, "InstallSSMSBoostPlus.exe");
                File.Copy(installerPath, targetExe, overwrite: true);

                Log($"[VersionCheck] 启动安装程序: {targetExe}");

                // 6. 启动安装程序（双击模式 = 自动下载并安装）
                Process.Start(new ProcessStartInfo
                {
                    FileName = targetExe,
                    UseShellExecute = true,
                    Verb = "runas",
                });

                Log("[VersionCheck] 关闭 SSMS 以进行更新");
                Environment.Exit(0);
            }
            finally
            {
                // 临时目录会保留（安装程序运行中），由安装程序或下次启动时清理
            }
        }
        catch (Exception ex)
        {
            Log($"[VersionCheck] 启动更新失败: {ex.Message}");
            System.Windows.MessageBox.Show(
                $"启动更新失败: {ex.Message}\n\n请手动从 Release 分支下载安装包。",
                "SSMSBoostPlus - 更新失败",
                System.Windows.MessageBoxButton.OK, System.Windows.MessageBoxImage.Error);
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

    private void Log(string message)
    {
        Debug.WriteLine(message);
        _logAction(message);
    }
}