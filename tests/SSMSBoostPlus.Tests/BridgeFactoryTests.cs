using System.Threading.Tasks;
using FluentAssertions;
using Xunit;
using SSMSBoostPlus.AI.Bridge;
using SSMSBoostPlus.Models;

namespace SSMSBoostPlus.Tests;

/// <summary>
/// AI 桥接工厂测试：验证 CLI→HTTP 降级逻辑。
/// 注意：BridgeFactoryService 是实例类可直接实例化；
/// BridgeFactory 静态类内部委托到此实例，行为等价。
/// </summary>
public class BridgeFactoryTests
{
    private readonly AIConfiguration _validConfig;

    public BridgeFactoryTests()
    {
        _validConfig = new AIConfiguration
        {
            ApiKey = "test-key",
            ApiEndpoint = "https://test.openai.com/v1",
            ModelName = "gpt-4",
            TimeoutSeconds = 30
        };
    }

    // ============================================================
    // 1. 直接创建 CLI 桥接：返回 CodebuddyCliBridge
    // ============================================================
    [Fact]
    public void CreateCliBridge_ReturnsCodebuddyCliBridge()
    {
        var factory = new BridgeFactoryService();
        var bridge = factory.CreateCliBridge();

        bridge.Should().BeOfType<CodebuddyCliBridge>();
    }

    // ============================================================
    // 2. 直接创建 HTTP API 桥接：返回 AIApiClientBridge
    // ============================================================
    [Fact]
    public void CreateHttpApiBridge_ReturnsAIApiClientBridge()
    {
        var factory = new BridgeFactoryService();
        var bridge = factory.CreateHttpApiBridge(_validConfig);

        bridge.Should().BeOfType<AIApiClientBridge>();
    }

    // ============================================================
    // 3. 向后兼容：静态 BridgeFactory.CreateCliBridge 仍正常
    // ============================================================
    [Fact]
    public void StaticBridgeFactory_CreateCliBridge_ReturnsCodebuddyCliBridge()
    {
        var bridge = BridgeFactory.CreateCliBridge(null!);
        bridge.Should().BeOfType<CodebuddyCliBridge>();
    }

    // ============================================================
    // 4. 向后兼容：静态 BridgeFactory.CreateHttpApiBridge 仍正常
    // ============================================================
    [Fact]
    public void StaticBridgeFactory_CreateHttpApiBridge_ReturnsAIApiClientBridge()
    {
        var bridge = BridgeFactory.CreateHttpApiBridge(_validConfig, null!);
        bridge.Should().BeOfType<AIApiClientBridge>();
    }
}