using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace SSMSBoostPlus.Deployment;

/// <summary>
/// 发布目标接口 — 所有发布程序必须实现此接口。
/// 设计目的：支持可扩展的发布策略，便于未来添加新的发布目标。
/// </summary>
public interface IDeploymentTarget
{
    /// <summary>发布目标名称（如 "直接部署到 SSMS"）</summary>
    string DisplayName { get; }

    /// <summary>唯一标识符（用于配置持久化，如 "ssms-direct"）</summary>
    string Id { get; }

    /// <summary>简要描述该发布方式</summary>
    string Description { get; }

    /// <summary>
    /// 检查该发布目标在当前环境下是否可用。
    /// 例如：SSMS 目录是否存在、是否有管理员权限等。
    /// </summary>
    Task<ResourceCheckResult> CheckAvailabilityAsync();

    /// <summary>
    /// 执行发布操作。
    /// </summary>
    /// <param name="context">发布上下文（包含构建产物路径、配置等）</param>
    /// <param name="progress">进度报告回调</param>
    /// <param name="cancellationToken">取消令牌</param>
    /// <returns>发布结果</returns>
    Task<DeploymentResult> DeployAsync(
        DeploymentContext context,
        IProgress<DeploymentProgress> progress,
        CancellationToken cancellationToken);
}

/// <summary>
/// 发布上下文 — 包含构建产物路径和配置信息。
/// </summary>
public class DeploymentContext
{
    /// <summary>解决方案目录</summary>
    public string SolutionDir { get; set; } = "";

    /// <summary>项目输出目录（编译产物所在路径）</summary>
    public string OutputDir { get; set; } = "";

    /// <summary>编译配置（Debug/Release）</summary>
    public string Configuration { get; set; } = "Release";

    /// <summary>目标框架</summary>
    public string TargetFramework { get; set; } = "net472";

    /// <summary>SSMS 版本号</summary>
    public string SsmsVersion { get; set; } = "22";

    /// <summary>超时时间（秒）</summary>
    public int TimeoutSeconds { get; set; } = 120;

    /// <summary>是否包含调试符号文件</summary>
    public bool IncludePdbFiles { get; set; } = true;
}

/// <summary>
/// 发布进度信息。
/// </summary>
public class DeploymentProgress
{
    /// <summary>当前步骤名称（如 "复制文件", "刷新配置"）</summary>
    public string StepName { get; set; } = "";

    /// <summary>进度百分比（0-100）</summary>
    public int PercentComplete { get; set; }

    /// <summary>详细状态信息</summary>
    public string StatusMessage { get; set; } = "";
}

/// <summary>
/// 发布结果。
/// </summary>
public class DeploymentResult
{
    /// <summary>是否成功</summary>
    public bool Success { get; set; }

    /// <summary>错误信息（如有）</summary>
    public string? ErrorMessage { get; set; }

    /// <summary>目标路径（发布到的位置）</summary>
    public string? TargetPath { get; set; }

    /// <summary>已发布的文件列表</summary>
    public List<string> DeployedFiles { get; set; } = new();

    /// <summary>耗时（毫秒）</summary>
    public long ElapsedMs { get; set; }
}

/// <summary>
/// 资源检查结果。
/// </summary>
public class ResourceCheckResult
{
    /// <summary>是否通过了所有检查</summary>
    public bool IsReady { get; set; }

    /// <summary>检查项目列表</summary>
    public List<CheckItem> Items { get; set; } = new();

    /// <summary>汇总信息</summary>
    public string Summary { get; set; } = "";

    public class CheckItem
    {
        public string Name { get; set; } = "";
        public bool Passed { get; set; }
        public string? Message { get; set; }
        public CheckSeverity Severity { get; set; } = CheckSeverity.Error;
    }

    public enum CheckSeverity { Info, Warning, Error }
}