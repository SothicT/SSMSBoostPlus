# ToolWindow Content 为 null 导致 COMException

## 元数据

- **异常类型**: `System.Runtime.InteropServices.COMException`
- **HRESULT**: `0x8000FFFF (E_UNEXPECTED)`
- **分类**: runtime
- **首次记录**: 2026-06-09
- **严重度**: 🔴 阻断

## 触发条件

1. 创建继承 `ToolWindowPane` 的 WPF 工具窗口
2. 构造函数中 `Content` 未设置或为 `null`
3. 通过 `Package.FindToolWindow(type, 0, true)` 首次创建窗口

## 关键方法

- `Microsoft.VisualStudio.Platform.WindowManagement.UIElementDocumentObject.Init`
- `Microsoft.VisualStudio.Shell.Package.FindToolWindow`
- `Microsoft.VisualStudio.Shell.Package.CreateToolWindow`

## 消息关键词

- `灾难性故障`
- `E_UNEXPECTED`
- `UIElementDocumentObject.Init`

## 根因

VS SDK 的 `CreateToolWindow` 在实例化 `ToolWindowPane` 后立即调用 `UIElementDocumentObject.Init` 初始化 WPF 文档对象。如果 `Content` 为 null，COM 层抛出 `E_UNEXPECTED`。

## 修复方案

在 ToolWindow 构造函数中设置占位 `Content`：

```csharp
public MyToolWindow() : base(null)
{
    Caption = "窗口标题";
    Content = new System.Windows.Controls.TextBlock { Text = "加载中..." };
}
```

后续通过 `InitializeContent` 方法替换为真实 UI。

## 预防措施

- 所有 ToolWindow 子类构造函数中必须设置非 null 的 `Content`