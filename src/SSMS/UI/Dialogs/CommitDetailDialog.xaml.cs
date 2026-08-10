using System;
using System.Collections.Generic;
using System.Windows;
using System.Windows.Controls;

namespace SSMSBoostPlus.SSMS.UI.Dialogs;

/// <summary>
/// 版本差异详情窗口 - 用于展示完整的 commit 差异列表，支持滚动。
/// </summary>
public partial class CommitDetailDialog : Window
{
    public CommitDetailDialog(string? localHash, string remoteHash, List<string> commits)
    {
        InitializeComponent();

        // 版本信息
        VersionInfoText.Text = $"本地版本: {(localHash?.Substring(0, 8) ?? "未知")}\n" +
                               $"远程版本: {remoteHash.Substring(0, 8)}";

        // Commit 标题
        CommitHeaderText.Text = $"更新内容（共 {commits.Count} 个提交）:";

        // 填充 commit 列表
        foreach (var commit in commits)
        {
            var displayText = commit;
            if (commit.Contains("\t"))
            {
                var parts = commit.Split(new[] { '\t' }, StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length >= 2)
                    displayText = $"{parts[0].Substring(0, 7)}  {parts[1].Trim()}";
                else
                    displayText = commit;
            }
            CommitList.Items.Add(displayText);
        }
    }

    private void OnCloseClick(object sender, RoutedEventArgs e)
    {
        Close();
    }
}