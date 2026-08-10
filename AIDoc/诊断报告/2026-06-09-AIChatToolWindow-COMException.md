# 诊断报告: AIChatToolWindow 创建失败 — COMException E_UNEXPECTED

| 属性 | 值 |
|------|-----|
| 时间 | 2026-06-09 10:05 |
| 来源 | SSMS 日志 (ssmsboostplus-2026060910.log) |
| 分类 | 运行时异常 |
| 严重度 | 🔴 阻断 |
| 知识库 | 未命中（新类型） |

## 原始日志

```
2026-06-09 10:05:53.905 [ERR] [AIChat] 打开 AI 聊天面板失败
System.Runtime.InteropServices.COMException (0x8000FFFF): 灾难性故障 (异常来自 HRESULT:0x8000FFFF (E_UNEXPECTED))
   在 Microsoft.VisualStudio.Platform.WindowManagement.UIElementDocumentObject.Init(Boolean initSize)
   在 Microsoft.VisualStudio.Platform.WindowManagement.DocumentObjectSite.InitializeDocumentObject(Object punkView)
   ...
   在 Microsoft.VisualStudio.Shell.Package.CreateToolWindow(Type toolWindowType, Int32 id, ...)
   在 Microsoft.VisualStudio.Shell.Package.FindToolWindow(Type toolWindowType, Int32 id, Boolean create, ...)
   在 SSMSBoostPlus.SSMS.Commands.AIChatCommand.<ExecuteAsync>d__5.MoveNext() 位置 AIChatCommand.cs:行号 57
```

## 根因分析

`FindToolWindow(create: true)` 内部调用 `CreateToolWindow`，VS SDK 创建 `AIChatToolWindow` 实例后立即尝试初始化 WPF 文档对象 (`UIElementDocumentObject.Init`)。此时 `Content` 为 `null`（构造函数仅设置了 `Caption`），导致 COM 层抛出 `E_UNEXPECTED`。

**调用链**：
1. `AIChatCommand.ExecuteAsync` → `Package.FindToolWindow(type, 0, true)`
2. → `Package.CreateToolWindow` → 实例化 `AIChatToolWindow()`
3. → VS WindowManagement 调用 `UIElementDocumentObject.Init` 读取 `Content`
4. → `Content == null` → `COMException 0x8000FFFF`

**为何 `InitializeContent` 未生效**：`InitializeContent` 在 `FindToolWindow` 返回后才调用，但异常发生在 `FindToolWindow` 内部。

### 涉及源码

| 文件 | 行号 | 角色 |
|------|------|------|
| `src/SSMS/UI/Chat/AIChatToolWindow.cs` | 23-27 | 构造函数，Content 未设置 |
| `src/SSMS/Commands/AIChatCommand.cs` | 62-65 | 触发 FindToolWindow |

## 修复建议

### 方案一（推荐）：构造时设置占位 Content

在 `AIChatToolWindow` 构造函数中设置一个占位 `TextBlock`，防止 VS 初始化时因 null Content 崩溃。`InitializeContent` 后续替换为真实 UI。

```csharp
// 文件: src/SSMS/UI/Chat/AIChatToolWindow.cs, 第 23-27 行
// 修改前:
public AIChatToolWindow()
    : base(null)
{
    Caption = "🤖 SSMSBoost++ AI 助手";
}

// 修改后:
public AIChatToolWindow()
    : base(null)
{
    Caption = "🤖 SSMSBoost++ AI 助手";
    // 设置占位 Content，防止 VS SDK 因 null Content 抛出 COMException
    Content = new System.Windows.Controls.TextBlock
    {
        Text = "正在加载 AI 助手...",
        HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
        VerticalAlignment = System.Windows.VerticalAlignment.Center,
        FontSize = 14,
        Foreground = System.Windows.Media.Brushes.Gray
    };
}
```

### 方案二（备选）：两阶段 FindToolWindow

先 `create: false` 查找已有窗口，失败后手动创建并设置 Content，再调用 `FindToolWindow`。

**不推荐**：增加复杂度，且 `FindToolWindow` 语义更清晰。

## 预防措施

- [ ] 所有 ToolWindow 子类构造函数中必须设置非 null 的 `Content`（至少占位元素）
- [ ] 新增 ToolWindow 后应立即在 SSMS 中验证窗口能否正常打开

## 诊断信息

- 诊断耗时: ~1s
- 代码搜索次数: 0（已有完整上下文）