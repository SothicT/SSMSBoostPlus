---
title: 差异按钮 AdornerLayer 方案 V8
date: 2026-07-15
author: 小Q
tags: [差异比对, 操作条, 对齐, AdornerLayer]
---

## 一、V7 失败分析

### V7 的问题

V7 使用 `InlineUIContainer` 将按钮放入段落：

```
Paragraph
├── InlineUIContainer (按钮)  ← 插入到开头
├── Run (" 24 | ")           ← 原有行号
└── Run ("PRINT 'a'")
```

**症状**：
1. 按钮和行号连在一起显示为 "▶ 24 | 24 | 24 |"（行号重复/错乱）
2. 按钮属于文档内容，会被光标选中、随文本复制
3. 点击按钮时可能误触文本编辑
4. 按钮挤占行号空间，影响阅读

**根本原因**：
- InlineUIContainer **是文档流的一部分**，必然影响布局
- 插入位置与原有行号冲突，导致显示错乱

## 二、V8 方案：AdornerLayer

### 核心思路

使用 WPF `AdornerLayer` 在段落**上方绘制按钮装饰器**，按钮：
- **不属于文档内容** — 不挤占行号、不被选中、不参与复制
- **自动跟随段落** — AdornerLayer 自动处理定位，无需手动同步
- **视觉独立** — 显示在段落左侧，像"悬浮"在行号区域

```
AdornerLayer 架构:
RichTextBox
└── FlowDocumentScrollViewer
    └── FlowDocument
        └── Paragraph (差异行)
            ├── Run (" 24 | ")      ← 行号（保持不变）
            ├── Run ("PRINT 'a'")   ← 代码（保持不变）
            └── [AdornerLayer]      ← 系统自动创建
                └── ButtonAdorner   ← 自定义装饰器
                    └── Button (▶)  ← 显示在段落左侧上方
```

### 为什么 AdornerLayer 能彻底解决

| 维度 | InlineUIContainer (V7) | AdornerLayer (V8) |
|------|------------------------|-------------------|
| 文档流 | 属于 Inline，参与布局 | **不属于文档流**，独立渲染 |
| 行号影响 | 挤占行号空间 | **不挤占**，显示在行号左侧 |
| 编辑影响 | 会被选中、复制 | **不影响**编辑操作 |
| 定位 | 手动插入，位置固定 | **自动跟随**段落位置 |
| 滚动 | 随段落滚动 | **自动跟随**滚动 |

## 三、详细修改方案

### 3.1 新增 DiffButtonAdorner 类

创建自定义 Adorner：

```csharp
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;

namespace SSMSBoostPlus.SSMS.UI.Dialogs;

/// <summary>
/// 差异行按钮装饰器：在段落左侧显示同步/撤销按钮。
/// 使用 AdornerLayer 渲染，不属于文档流，不影响编辑。
/// </summary>
public class DiffButtonAdorner : Adorner
{
    private readonly Button _button;
    private readonly Paragraph _targetParagraph;

    public DiffButtonAdorner(Paragraph adornedElement, Button button)
        : base(adornedElement)
    {
        _targetParagraph = adornedElement;
        _button = button;
        
        // 按钮样式
        _button.Width = 20;
        _button.Height = 18;
        _button.HorizontalAlignment = HorizontalAlignment.Left;
        _button.VerticalAlignment = VerticalAlignment.Top;
        
        AddVisualChild(_button);
        AddLogicalChild(_button);
    }

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index)
    {
        if (index != 0) throw new ArgumentOutOfRangeException();
        return _button;
    }

    protected override Size MeasureOverride(Size constraint)
    {
        _button.Measure(constraint);
        return _button.DesiredSize;
    }

    /// <summary>
    /// 定位按钮：放在段落左侧（行号区域）
    /// </summary>
    protected override Size ArrangeOverride(Size finalSize)
    {
        // 获取段落相对于 RichTextBox 的位置
        var rect = _targetParagraph.ContentStart.GetCharacterRect(LogicalDirection.Forward);
        
        // 按钮放在段落左侧（留出 2px 边距）
        double left = 2;
        double top = rect.Top;
        
        _button.Arrange(new Rect(left, top, _button.DesiredSize.Width, _button.DesiredSize.Height));
        return finalSize;
    }

    public Paragraph TargetParagraph => _targetParagraph;
    public Button Button => _button;
}
```

### 3.2 重写 PopulateDiffButtons

```csharp
/// <summary>
/// 为右侧面板的差异段落附加 Adorner 按钮。
/// 使用 AdornerLayer 渲染，按钮不属于文档流，不影响编辑。
/// </summary>
private void PopulateDiffButtons()
{
    // 清除现有 Adorner
    ClearDiffButtonAdorners();

    // 获取右侧面板的 AdornerLayer
    var adornerLayer = AdornerLayer.GetAdornerLayer(RightViewer);
    if (adornerLayer == null) return;

    // 为每个 diff hunk 创建按钮并附加到对应段落
    for (int h = 0; h < _diffHunks.Count; h++)
    {
        var hunk = _diffHunks[h];
        int blockIndex = hunk.BlockStartIndex;
        
        var paragraph = GetParagraphAtIndex(RightViewer.Document, blockIndex);
        if (paragraph == null) continue;

        var button = CreateSyncButton(h, hunk.HunkChangeType);
        _actionButtons[blockIndex] = button;

        // 创建 Adorner 并附加到段落
        var adorner = new DiffButtonAdorner(paragraph, button);
        adornerLayer.Add(adorner);
        _diffButtonAdorners.Add(adorner);
    }

    // 为已同步的 hunk 创建恢复按钮
    foreach (var kvp in _hunkInsertedBlocks)
    {
        int syncId = kvp.Key;
        var syncState = kvp.Value;
        
        int blockIndex = syncState.Inserted.Count > 0
            ? syncState.Inserted.First()
            : syncState.OrigStart;

        if (_actionButtons.ContainsKey(blockIndex)) continue;

        var paragraph = GetParagraphAtIndex(RightViewer.Document, blockIndex);
        if (paragraph == null) continue;

        var button = CreateUndoButton(syncId);
        _actionButtons[blockIndex] = button;

        var adorner = new DiffButtonAdorner(paragraph, button);
        adornerLayer.Add(adorner);
        _diffButtonAdorners.Add(adorner);
    }
}

// 存储所有 Adorner 以便清除
private readonly List<DiffButtonAdorner> _diffButtonAdorners = new();

/// <summary>
/// 清除所有差异按钮 Adorner
/// </summary>
private void ClearDiffButtonAdorners()
{
    var adornerLayer = AdornerLayer.GetAdornerLayer(RightViewer);
    if (adornerLayer != null)
    {
        foreach (var adorner in _diffButtonAdorners)
        {
            adornerLayer.Remove(adorner);
        }
    }
    _diffButtonAdorners.Clear();
    _actionButtons.Clear();
}
```

### 3.3 窗口关闭时清理

```csharp
private void OnClosed(object sender, EventArgs e)
{
    _isLoaded = false;
    _renumberTimer?.Stop();
    _renumberTimer = null;
    
    // 清除 Adorner
    ClearDiffButtonAdorners();
    
    // ... 其他清理代码
}
```

## 四、优势对比

| 维度 | V7 (InlineUIContainer) | V8 (AdornerLayer) |
|------|------------------------|-------------------|
| 文档流 | 属于 Inline，参与布局 | **不属于文档流** |
| 行号影响 | 挤占、重复显示 | **无影响** |
| 编辑影响 | 会被选中、复制 | **不影响** |
| 定位 | 手动插入 | **自动跟随** |
| 滚动同步 | 随段落滚动 | **自动跟随** |
| 视觉 | 与文本混在一起 | **独立显示在行号左侧** |

## 五、风险与注意事项

### 5.1 AdornerLayer 获取时机

`AdornerLayer.GetAdornerLayer` 需要在视觉树构建完成后调用，通常在 `Loaded` 事件后。

### 5.2 段落生命周期

段落被移除时（UndoHunk），对应的 Adorner 会自动失效，需要手动清理 `_diffButtonAdorners` 列表。

### 5.3 Z-Index

Adorner 显示在所有内容之上，可能遮挡行号。需要调整按钮位置（放在行号左侧或行号上方）。

## 六、验证清单

- [ ] 编译通过（0 错误）
- [ ] 打开差异比对弹窗 → 按钮显示在行号左侧
- [ ] 按钮与差异行**天然对齐**（Adorner 自动定位）
- [ ] 按钮**不影响编辑**（不属于文档流，不会被选中）
- [ ] 行号显示正常，**无重复/挤占**
- [ ] 滚动时按钮**自动跟随**段落
- [ ] 点击同步 → 按钮变为 ↺，位置正确
- [ ] 点击恢复 → 按钮变回 ▶，位置正确
