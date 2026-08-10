using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace SSMSBoostPlus.Deployment;

/// <summary>
/// 发布管道 — 自动化构建→检查→发布的主协调器。
/// 
/// 工作流程：
///   1. 编译验证（Build）
///   2. 资源检查（ResourceCheck）
///   3. 发现可用发布目标（DiscoverTargets）
///   4. 执行发布（Deploy）
///   5. 结果通知（Notify）
/// 
/// 参考依据：
///   - build.bat 编译流程
///   - deploy-to-ssms.bat 部署流程
/// </summary>
public class DeploymentPipeline
{
    private readonly BuildService _buildService;
    private readonly ResourceChecker _resourceChecker;
    private readonly IDeploymentLogger _logger;
    private readonly IEnumerable<IDeploymentTarget> _targets;

    /// <summary>管道配置</summary>
    public PipelineConfig Config { get; set; }

    /// <summary>当前执行阶段</summary>
    public PipelineStage CurrentStage { get; private set; } = PipelineStage.Idle;

    /// <summary>阶段变更事件（可用于 UI 进度显示）</summary>
    public event EventHandler<PipelineStageEventArgs>? OnStageChanged;

    /// <summary>错误/异常事件（实时警报）</summary>
    public event EventHandler<PipelineErrorEventArgs>? OnError;

    /// <summary>上次编译结果</summary>
    public BuildResult? LastBuildResult { get; private set; }

    /// <summary>上次发布结果</summary>
    public DeploymentResult? LastDeployResult { get; private set; }

    public DeploymentPipeline(
        BuildService buildService,
        ResourceChecker resourceChecker,
        IDeploymentLogger logger,
        IEnumerable<IDeploymentTarget> targets)
    {
        _buildService = buildService;
        _resourceChecker = resourceChecker;
        _logger = logger;
        _targets = targets;
        Config = new PipelineConfig();
    }

    /// <summary>
    /// 执行完整的发布流程（构建→检查→发布）。
    /// </summary>
    public async Task<PipelineResult> RunAsync(
        DeploymentContext context,
        IProgress<DeploymentProgress> progress,
        string? targetId = null)
    {
        var result = new PipelineResult();
        var cts = new CancellationTokenSource();
        cts.CancelAfter(TimeSpan.FromSeconds(context.TimeoutSeconds));

        try
        {
            // === 阶段1: 编译验证 ===
            SetStage(PipelineStage.Building);
            _logger.LogInfo("=" .PadRight(60, '='));
            _logger.LogInfo("[管道] 🚀 开始发布流程");
            _logger.LogInfo("=" .PadRight(60, '='));

            progress.Report(new DeploymentProgress
            { StepName = "编译验证", PercentComplete = 0, StatusMessage = "正在编译项目..." });

            LastBuildResult = await _buildService.BuildAsync(context.SolutionDir, context.Configuration);
            result.BuildResult = LastBuildResult;

            if (!LastBuildResult.Success)
            {
                _logger.LogError("[管道] ❌ 编译失败，终止发布流程");
                OnError?.Invoke(this, new PipelineErrorEventArgs
                {
                    Stage = CurrentStage,
                    Message = $"编译失败: {LastBuildResult.ErrorMessage}",
                    Exception = null
                });
                result.Success = false;
                result.ErrorMessage = LastBuildResult.ErrorMessage;
                return result;
            }

            // 验证输出文件
            var outputDir = Path.Combine(
                Path.GetDirectoryName(context.SolutionDir) ?? "",
                "SSMS", "bin", context.Configuration, context.TargetFramework);
            context.OutputDir = outputDir;

            if (!_buildService.VerifyOutputFiles(outputDir, "SSMSBoostPlus.SSMS.dll", "SSMSBoostPlus.Core.dll"))
            {
                _logger.LogError("[管道] ❌ 编译产物验证失败");
                result.Success = false;
                result.ErrorMessage = "编译产物不完整";
                return result;
            }

            cts.Token.ThrowIfCancellationRequested();

            // === 阶段2: 资源检查 ===
            SetStage(PipelineStage.CheckingResources);
            progress.Report(new DeploymentProgress
            { StepName = "资源检查", PercentComplete = 30, StatusMessage = "检查发布所需资源..." });

            var resourceCheck = await _resourceChecker.CheckAsync(context);
            result.ResourceCheckResult = resourceCheck;

            if (!resourceCheck.IsReady)
            {
                var errors = resourceCheck.Items
                    .Where(i => !i.Passed && i.Severity == ResourceCheckResult.CheckSeverity.Error)
                    .Select(i => i.Message);
                var errorMsg = $"资源检查未通过:\n{string.Join("\n", errors)}";
                _logger.LogError($"[管道] ❌ {errorMsg}");
                OnError?.Invoke(this, new PipelineErrorEventArgs
                { Stage = CurrentStage, Message = errorMsg, Exception = null });
                result.Success = false;
                result.ErrorMessage = errorMsg;
                return result;
            }

            cts.Token.ThrowIfCancellationRequested();

            // === 阶段3: 选择发布目标 ===
            SetStage(PipelineStage.SelectingTarget);
            progress.Report(new DeploymentProgress
            { StepName = "选择发布目标", PercentComplete = 50, StatusMessage = "扫描可用发布程序..." });

            var availableTargets = DiscoverAvailableTargets();
            result.AvailableTargets = availableTargets;

            IDeploymentTarget? selectedTarget = null;

            if (!string.IsNullOrWhiteSpace(targetId))
            {
                selectedTarget = availableTargets.FirstOrDefault(t => t.Id == targetId);
            }

            // 如果未指定目标，使用默认目标
            if (selectedTarget == null && !string.IsNullOrWhiteSpace(Config.DefaultTargetId))
            {
                selectedTarget = availableTargets.FirstOrDefault(t => t.Id == Config.DefaultTargetId);
                if (selectedTarget != null)
                {
                    _logger.LogInfo($"[管道] 使用默认发布目标: {selectedTarget.DisplayName}");
                }
            }

            // 如果仍然没有目标，取第一个可用
            selectedTarget ??= availableTargets.FirstOrDefault();

            if (selectedTarget == null)
            {
                var msg = "没有可用的发布目标";
                _logger.LogError($"[管道] ❌ {msg}");
                result.Success = false;
                result.ErrorMessage = msg;
                return result;
            }

            _logger.LogInfo($"[管道] 选定发布目标: {selectedTarget.DisplayName} ({selectedTarget.Id})");

            cts.Token.ThrowIfCancellationRequested();

            // === 阶段4: 执行发布 ===
            SetStage(PipelineStage.Deploying);
            progress.Report(new DeploymentProgress
            { StepName = "执行发布", PercentComplete = 60, StatusMessage = $"正在通过 {selectedTarget.DisplayName} 发布..." });

            LastDeployResult = await selectedTarget.DeployAsync(context, progress, cts.Token);
            result.DeployResult = LastDeployResult;

            if (!LastDeployResult.Success)
            {
                _logger.LogError($"[管道] ❌ 发布失败: {LastDeployResult.ErrorMessage}");
                OnError?.Invoke(this, new PipelineErrorEventArgs
                { Stage = CurrentStage, Message = $"发布失败: {LastDeployResult.ErrorMessage}", Exception = null });
                result.Success = false;
                result.ErrorMessage = LastDeployResult.ErrorMessage;
                return result;
            }

            cts.Token.ThrowIfCancellationRequested();

            // === 阶段5: 完成 ===
            SetStage(PipelineStage.Completed);
            result.Success = true;
            result.SelectedTarget = selectedTarget;

            _logger.LogInfo("=" .PadRight(60, '='));
            _logger.LogInfo("[管道] ✅ 发布流程全部完成!");
            _logger.LogInfo($"[管道]   编译: {(LastBuildResult?.Success == true ? "✅ 通过" : "❌ 失败")} ({LastBuildResult?.ElapsedMs ?? 0}ms)");
            _logger.LogInfo($"[管道]   发布: {selectedTarget.DisplayName}");
            _logger.LogInfo($"[管道]   目标: {LastDeployResult.TargetPath}");
            _logger.LogInfo($"[管道]   文件: {LastDeployResult.DeployedFiles.Count} 个");
            _logger.LogInfo($"[管道]   总耗时: {result.TotalElapsedMs}ms");
            _logger.LogInfo("=" .PadRight(60, '='));

            progress.Report(new DeploymentProgress
            {
                StepName = "全部完成",
                PercentComplete = 100,
                StatusMessage = $"✅ 发布完成! 请重启 SSMS 加载扩展"
            });
        }
        catch (OperationCanceledException)
        {
            SetStage(PipelineStage.Cancelled);
            result.Success = false;
            result.ErrorMessage = "流程已超时或被取消";
            _logger.LogWarning($"[管道] ⚠️ 流程超时或被取消 ({context.TimeoutSeconds}秒)");
            OnError?.Invoke(this, new PipelineErrorEventArgs
            { Stage = CurrentStage, Message = result.ErrorMessage, Exception = null });
        }
        catch (Exception ex)
        {
            SetStage(PipelineStage.Failed);
            result.Success = false;
            result.ErrorMessage = $"管道异常: {ex.Message}";
            _logger.LogError($"[管道] ❌ {result.ErrorMessage}");
            OnError?.Invoke(this, new PipelineErrorEventArgs
            { Stage = CurrentStage, Message = result.ErrorMessage, Exception = ex });
        }
        finally
        {
            cts.Dispose();
        }

        return result;
    }

    /// <summary>
    /// 扫描所有已注册的发布目标，返回当前环境下可用的目标列表。
    /// </summary>
    public List<IDeploymentTarget> DiscoverAvailableTargets()
    {
        var available = new List<IDeploymentTarget>();

        foreach (var target in _targets)
        {
            try
            {
                var check = target.CheckAvailabilityAsync().GetAwaiter().GetResult();
                if (check.IsReady)
                {
                    available.Add(target);
                    _logger.LogDebug($"[管道] 发现可用发布目标: {target.DisplayName} ({target.Id})");
                }
                else
                {
                    _logger.LogDebug($"[管道] 发布目标不可用: {target.DisplayName} - {check.Summary}");
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[管道] 检查发布目标时出错: {target.DisplayName}: {ex.Message}");
            }
        }

        _logger.LogInfo($"[管道] 共发现 {available.Count}/{_targets.Count()} 个可用发布目标");
        return available;
    }

    /// <summary>
    /// 设置默认发布目标（后续自动使用）。
    /// </summary>
    public void SetDefaultTarget(string targetId)
    {
        Config.DefaultTargetId = targetId;
        _logger.LogInfo($"[管道] 默认发布目标已设置为: {targetId}");
    }

    private void SetStage(PipelineStage stage)
    {
        CurrentStage = stage;
        OnStageChanged?.Invoke(this, new PipelineStageEventArgs { Stage = stage });
    }
}

/// <summary>
/// 管道配置。
/// </summary>
public class PipelineConfig
{
    /// <summary>默认发布目标 ID</summary>
    public string? DefaultTargetId { get; set; }

    /// <summary>默认超时时间（秒）</summary>
    public int DefaultTimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// 管道执行阶段。
/// </summary>
public enum PipelineStage
{
    Idle,
    Building,
    CheckingResources,
    SelectingTarget,
    Deploying,
    Completed,
    Cancelled,
    Failed
}

/// <summary>
/// 管道执行结果。
/// </summary>
public class PipelineResult
{
    public bool Success { get; set; }
    public string? ErrorMessage { get; set; }
    public BuildResult? BuildResult { get; set; }
    public ResourceCheckResult? ResourceCheckResult { get; set; }
    public DeploymentResult? DeployResult { get; set; }
    public IDeploymentTarget? SelectedTarget { get; set; }
    public List<IDeploymentTarget> AvailableTargets { get; set; } = new();

    public long TotalElapsedMs =>
        (BuildResult?.ElapsedMs ?? 0) +
        (DeployResult?.ElapsedMs ?? 0);
}

/// <summary>
/// 阶段变更事件参数。
/// </summary>
public class PipelineStageEventArgs : EventArgs
{
    public PipelineStage Stage { get; set; }
}

/// <summary>
/// 管道错误事件参数。
/// </summary>
public class PipelineErrorEventArgs : EventArgs
{
    public PipelineStage Stage { get; set; }
    public string Message { get; set; } = "";
    public Exception? Exception { get; set; }
}