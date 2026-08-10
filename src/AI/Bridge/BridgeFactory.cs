using System.Threading.Tasks;
using SSMSBoostPlus.AI.Parsing;
using SSMSBoostPlus.AI.Prompts;
using SSMSBoostPlus.Models;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.AI.Bridge;

/// <summary>
/// 桥接工厂类，负责根据配置和环境自动选择最佳的 AI 桥接实现。
/// 
/// 选择策略（按优先级）：
/// 1. 用户显式配置的 CodeBuddy CLI — 如果 CodebuddyCliPath 已设置且可用
/// 2. 用户配置的 HTTP API        — 如果 ApiEndpoint 已设置（自定义 OpenAI 兼容 API）
/// 3. 内置默认 AI API            — 零配置，自动使用内置默认接口
/// 
/// 注意：此静态类保留向后兼容，内部委托给 BridgeFactoryService 实例。
///      新代码推荐通过构造函数注入 IBridgeFactory 以支持单元测试 Mock。
/// </summary>
public static class BridgeFactory
{
    /// <summary>
    /// 创建最佳可用的 AI 桥接实例。
    /// 仅当用户显式配置了 CLI 路径时才尝试 CLI，否则直接使用 HTTP API（用户配置或内置默认）。
    /// </summary>
    /// <param name="config">AI 配置</param>
    /// <param name="logger">日志服务</param>
    public static async Task<ICodebuddyBridge> CreateBestAvailableBridgeAsync(AIConfiguration config, ILoggerService logger)
    {
        var service = new BridgeFactoryService(logger);
        return await service.CreateBestAvailableBridgeAsync(config);
    }

    /// <summary>
    /// 直接创建 HTTP API 桥接（不尝试 CLI）。
    /// </summary>
    /// <param name="config">AI 配置</param>
    /// <param name="logger">日志服务</param>
    public static ICodebuddyBridge CreateHttpApiBridge(AIConfiguration config, ILoggerService logger)
    {
        var service = new BridgeFactoryService(logger);
        return service.CreateHttpApiBridge(config);
    }

    /// <summary>
    /// 直接创建 CLI 桥接（不检测可用性）。
    /// </summary>
    /// <param name="logger">日志服务</param>
    public static ICodebuddyBridge CreateCliBridge(ILoggerService logger)
    {
        var service = new BridgeFactoryService(logger);
        return service.CreateCliBridge();
    }
}
