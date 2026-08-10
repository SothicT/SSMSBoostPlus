namespace SSMSBoostPlus.AI.Models;

/// <summary>
/// Prompt 模板类型枚举，定义五种 AI 辅助功能。
/// </summary>
public enum PromptTemplate
{
    /// <summary>SQL 代码建议：语法错误检测 + 代码精简 + 性能优化 + 安全审查</summary>
    Suggest,

    /// <summary>SQL 注释生成：自动为 T-SQL 代码添加中文注释</summary>
    Comment,

    /// <summary>SQL 格式化：按 Microsoft/Google 风格格式化 SQL 代码</summary>
    Format,

    /// <summary>SQL 性能分析：执行计划解读 + 缺失索引 + 瓶颈定位 + 优化方案</summary>
    Analyze,

    /// <summary>自由对话：AI 对话面板的自由聊天模式，通用数据库技术问答</summary>
    Chat
}
