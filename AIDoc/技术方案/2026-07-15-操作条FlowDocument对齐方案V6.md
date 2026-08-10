---
title: 操作条 FlowDocument 对齐方案 V6
date: 2026-07-15
author: 小Q
tags: [差异比对, 操作条, 对齐, FlowDocument, 架构重构]
---

## 一、问题回顾

### V1~V5 修复历程

| 版本 | 方案 | 结果 |
|------|------|------|
| V1~V3 | Canvas + GetCharacterRect 精确定位 | GetCharacterRect 返回值不稳定 |
| V4 | Canvas + PrecomputeBlockYPositions + LineStackingStrategy | 高度假设仍有误差 |
| V5 | StackPanel + 固定高度格子（18px/24px） | **仍然错位** |

### V5 失败根因

V5 用 StackPanel 替代 Canvas，消除了手动 Y 坐标计算，但**仍然在做高度假设**：

```
StackPanel 格子高度 = 18px（假设）  ←  这仍然是"高度计算"
FlowDocument 段落高度 = ?px（实际渲染）  ←  与假设值存在微小差异
```

差异来源：
- FlowDocument 内部布局开销（段落间距、文档内边距等）
- DPI 缩舍入差异
- 字体度量在 StackPanel 和 FlowDocument 中的不同处理

**累积效应**：每段差 0.1~0.5px，20 段后偏移 2~10px，越往下越明显。

### 用户观察到的症状

> "同步按钮还是没有和差异块对齐……随着我向下滑动到底部，同步按钮和差异块对齐了"

这完全符合**总高度不匹配**的特征：

```
StackPanel 总高度 = 18 × N = 900px（假设）
FlowDocument 总高度 = 18.2 × N = 910px（实际）

滚动同步使用绝对偏移复制：
  顶部（offset=0）：两者都从 0 开始 → 看似对齐，但第 10 段已有 2px 偏移
  中部（offset=450）：StackPanel 显示 50% 位置，FlowDocument 显示 49.5% 位置 → 明显错位
  底部（offset=max）：两者都到达最大偏移 → "对齐了"
```

### 核心矛盾

```
两个独立的布局引擎（StackPanel vs FlowDocument）
  → 两套独立的高度计算
  → 高度永远无法完全一致
  → 累积误差无法消除
```

## 二、开源项目调研

### DiffPlex WPF SideBySideDiffViewer

DiffPlex 是 .NET 生态最成熟的 WPF 差异比对控件。

**布局策略**：
- 左右面板各使用一个 `InternalLinesViewer`（继承自 `Control`）
- 每行使用 `StackPanel` + `TextBlock` 渲染
- 左右面板使用**相同的控件和相同的布局策略**
- 对齐由**同构布局**保证——两侧使用完全相同的渲染机制

**关键启示**：DiffPlex 之所以能完美对齐，不是因为计算精确，而是因为**左右使用相同的渲染引擎**。只要两侧使用相同的布局机制，高度就天然一致。

### 我们的特殊约束

与 DiffPlex 不同，我们的右侧面板需要**行内编辑**（RichTextBox + FlowDocument），不能简化为 StackPanel + TextBlock。

因此，操作条也必须使用 FlowDocument，才能与右侧面板的渲染引擎匹配。

## 三、V6 方案：FlowDocument 操作条

### 核心思路

**一句话**：操作条从 StackPanel 改为 RichTextBox（只读），使用与右侧面板完全相同的 FlowDocument 配置。段落高度由 WPF FlowDocument 引擎统一计算，天然一致。

```
❌ V5: StackPanel（独立布局引擎） vs FlowDocument（右侧面板）
      → 两套高度计算 → 累积误差

✅ V6: FlowDocument（操作条） vs FlowDocument（右侧面板）
      → 同一渲染引擎 → 高度天然一致
```

### 为什么这能彻底解决问题

| 维度 | V5 (StackPanel) | V6 (FlowDocument) |
|------|----------------|-------------------|
| 布局引擎 | StackPanel（Grid 子元素堆叠） | FlowDocument（与右侧面板相同） |
| 高度来源 | 假设值 18px/24px | WPF FlowDocument 引擎计算 |
| 高度匹配 | 可能不匹配（累积误差） | **天然一致**（同一引擎、同一配置） |
| 滚动同步 | 绝对偏移复制（受总高度影响） | 绝对偏移复制（总高度相同，完美匹配） |
| DPI/字体变化 | StackPanel 和 FlowDocument 处理不同 | 两者处理完全相同 |

### 架构图

```
V5 架构（三套布局引擎）:
┌─────────────┐  ┌───────────┐  ┌─────────────┐
│ FlowDocument │  │ StackPanel │  │ FlowDocument │
│ (LeftViewer) │  │ (ActionStr)│  │ (RightViewer)│
└─────────────┘  └───────────┘  └─────────────┘
  独立高度计算     独立高度计算     独立高度计算
                  ↑ 与左右不匹配

V6 架构（统一布局引擎）:
┌─────────────┐  ┌─────────────┐  ┌─────────────┐
│ FlowDocument │  │ FlowDocument │  │ FlowDocument │
│ (LeftViewer) │  │ (ActionStrip)│  │ (RightViewer)│
└─────────────┘  └─────────────┘  └─────────────┘
  WPF 统一计算     WPF 统一计算     WPF 统一计算
                  ↑ 与右侧完全一致
```

## 四、详细修改方案

### 4.1 XAML 修改

**替换操作条内容容器**：

```xml
<!-- V5: StackPanel -->
<ScrollViewer x:Name="ActionStripScroller" Grid.Row="1" ...>
    <StackPanel x:Name="ActionStripPanel" Width="24" />
</ScrollViewer>

<!-- V6: RichTextBox（只读，与右侧面板相同的渲染引擎） -->
<RichTextBox x:Name="ActionStripViewer" Grid.Row="1"
             IsReadOnly="True"
             IsDocumentEnabled="False"
             VerticalScrollBarVisibility="Hidden"
             HorizontalScrollBarVisibility="Disabled"
             FontFamily="Consolas"
             FontSize="12"
             Background="#F5F5F5"
             BorderThickness="0"
             Padding="0"
             Focusable="False">
    <RichTextBox.Resources>
        <Style TargetType="Paragraph">
            <Setter Property="Margin" Value="0"/>
            <Setter Property="LineHeight" Value="18"/>
            <Setter Property="LineStackingStrategy" Value="BlockLineHeight"/>
            <Setter Property="Foreground" Value="#1E1E1E"/>
        </Style>
    </RichTextBox.Resources>
</RichTextBox>
```

**关键配置**（必须与右侧面板完全一致）：
- `FontFamily="Consolas"` — 相同字体
- `FontSize="12"` — 相同字号
- `Padding="0"` — 相同内边距
- `BorderThickness="0"` — 相同边框
- Paragraph Style: `Margin=0, LineHeight=18, LineStackingStrategy=BlockLineHeight`

**注意**：不再需要外层 `ScrollViewer`（`ActionStripScroller`），RichTextBox 有自己的内部 ScrollViewer。

### 4.2 PopulateActionStrip 重写

```csharp
private void PopulateActionStrip()
{
    _actionButtons.Clear();

    RightViewer.UpdateLayout();
    LeftViewer.UpdateLayout();

    // 构建操作条 FlowDocument——与右侧面板相同的配置
    var doc = new FlowDocument
    {
        PageWidth = double.NaN,
        ColumnWidth = double.PositiveInfinity,
        PagePadding = new Thickness(0),
        FontFamily = new FontFamily("Consolas"),
        FontSize = 12,
        LineHeight = 18,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight
    };

    // 为右侧面板的每个段落创建对应的操作条段落
    var rightParas = RightViewer.Document.Blocks.OfType<Paragraph>().ToList();
    for (int i = 0; i < rightParas.Count; i++)
    {
        double lineHeight = _placeholderBlocks.Contains(i) ? 24.0 : 18.0;
        var para = new Paragraph
        {
            Margin = new Thickness(0),
            LineHeight = lineHeight,
            LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
            Background = Brushes.Transparent,
            TextAlignment = TextAlignment.Center
        };

        // 占位段落（"—— N 行未变更 ——"）使用特殊高度
        if (_placeholderBlocks.Contains(i))
        {
            para.Inlines.Add(new Run("") { FontSize = 10 });
        }
        else
        {
            // 默认空段落（无按钮）
            para.Inlines.Add(new Run(""));
        }

        doc.Blocks.Add(para);
    }

    ActionStripViewer.Document = doc;

    // 为 _diffHunks 中未同步的 hunk 添加同步按钮
    for (int h = 0; h < _diffHunks.Count; h++)
    {
        var hunk = _diffHunks[h];
        int blockIndex = hunk.BlockStartIndex;
        var button = CreateSyncButton(h, hunk.HunkChangeType);
        _actionButtons[blockIndex] = button;
        PlaceButtonInActionStrip(blockIndex, button);
    }

    // 为 _hunkInsertedBlocks 中已同步的 hunk 添加恢复按钮
    foreach (var kvp in _hunkInsertedBlocks)
    {
        int syncId = kvp.Key;
        var syncState = kvp.Value;
        int blockIndex = syncState.Inserted.Count > 0
            ? syncState.Inserted.First()
            : syncState.OrigStart;
        if (_actionButtons.ContainsKey(blockIndex)) continue;

        var button = CreateUndoButton(syncId);
        _actionButtons[blockIndex] = button;
        PlaceButtonInActionStrip(blockIndex, button);
    }

    // 同步滚动偏移
    SyncActionStripScroll();
}

/// <summary>
/// 将按钮放入操作条 FlowDocument 的指定段落中。
/// 使用 InlineUIContainer 使按钮成为段落内容的一部分。
/// </summary>
private void PlaceButtonInActionStrip(int blockIndex, Button button)
{
    var paras = ActionStripViewer.Document.Blocks.OfType<Paragraph>().ToList();
    if (blockIndex < 0 || blockIndex >= paras.Count) return;

    var para = paras[blockIndex];
    para.Inlines.Clear();
    var container = new InlineUIContainer(button)
    {
        BaselineAlignment = BaselineAlignment.Center
    };
    para.Inlines.Add(container);
}
```

### 4.3 滚动同步修改

```csharp
private void OnScrollChanged(object sender, ScrollChangedEventArgs e)
{
    if (_scrollSyncing || !_isLoaded) return;
    _scrollSyncing = true;

    try
    {
        var sourceSv = FindScrollViewer(sender as RichTextBox);
        var targetSv = FindScrollViewer(sender == LeftViewer ? RightViewer : LeftViewer);
        if (sourceSv == null || targetSv == null) return;

        if (e.ExtentHeightChange == 0 && e.ExtentWidthChange == 0)
        {
            // 左右面板比例同步（保持不变）
            double sourceVMax = Math.Max(1, sourceSv.ScrollableHeight);
            double vRatio = sourceSv.VerticalOffset / sourceVMax;
            double targetVOffset = vRatio * Math.Max(1, targetSv.ScrollableHeight);
            targetSv.ScrollToVerticalOffset(targetVOffset);

            double sourceHMax = Math.Max(1, sourceSv.ScrollableWidth);
            double hRatio = sourceSv.HorizontalOffset / sourceHMax;
            double targetHOffset = hRatio * Math.Max(1, targetSv.ScrollableWidth);
            targetSv.ScrollToHorizontalOffset(targetHOffset);
        }

        // 操作条同步：绝对偏移复制
        // 因为操作条和右侧面板使用相同的 FlowDocument 配置，
        // ExtentHeight 相同，绝对偏移复制可精确对齐
        var rightSv = sender == RightViewer ? sourceSv : targetSv;
        if (rightSv != null)
        {
            SyncActionStripScroll(rightSv.VerticalOffset);
        }

        UpdateMinimapViewport(
            sourceSv.VerticalOffset,
            sourceSv.VerticalOffset + sourceSv.ViewportHeight);
    }
    finally
    {
        _scrollSyncing = false;
    }
}

/// <summary>
/// 同步操作条 RichTextBox 的滚动偏移到指定值。
/// </summary>
private void SyncActionStripScroll(double? verticalOffset = null)
{
    var actionSv = FindScrollViewer(ActionStripViewer);
    if (actionSv == null) return;

    double offset = verticalOffset ?? FindScrollViewer(RightViewer)?.VerticalOffset ?? 0;
    actionSv.ScrollToVerticalOffset(offset);
}
```

### 4.4 SyncHunk / UndoHunk 修改

在 SyncHunk 中插入新段落时，同步在操作条 FlowDocument 中插入对应段落：

```csharp
// 在 SyncHunk 方法中，插入右侧新段落的同时，
// 在操作条 FlowDocument 中插入对应空段落（保持段落索引一致）

// 插入右侧新段落后：
var actionParas = ActionStripViewer.Document.Blocks.OfType<Paragraph>().ToList();
if (idx < actionParas.Count)
{
    var actionPara = new Paragraph
    {
        Margin = new Thickness(0),
        LineHeight = 18,
        LineStackingStrategy = LineStackingStrategy.BlockLineHeight,
        Background = Brushes.Transparent,
        TextAlignment = TextAlignment.Center
    };
    actionPara.Inlines.Add(new Run(""));
    ActionStripViewer.Document.Blocks.InsertBefore(actionParas[idx], actionPara);
}
```

在 UndoHunk 中删除段落时，同步删除操作条 FlowDocument 的对应段落：

```csharp
// 删除右侧段落后，同步删除操作条段落
var actionParas = ActionStripViewer.Document.Blocks.OfType<Paragraph>().ToList();
for (int i = 0; i < removedCount; i++)
{
    if (insertAt < actionParas.Count)
    {
        ActionStripViewer.Document.Blocks.Remove(actionParas[insertAt]);
        actionParas.RemoveAt(insertAt);
    }
}
```

### 4.5 删除的代码

| 删除项 | 原因 |
|--------|------|
| `_actionStripSlots` 列表 | 不再使用 StackPanel 格子 |
| `CreateActionSlot()` 方法 | 不再创建 Grid 格子 |
| `ActionStripPanel`（XAML） | 替换为 `ActionStripViewer` |
| `ActionStripScroller`（XAML） | RichTextBox 有内部 ScrollViewer |

### 4.6 修改清单

| # | 文件 | 修改 |
|---|------|------|
| 1 | AIDiffDialog.xaml | `ScrollViewer + StackPanel` → `RichTextBox`（操作条区域） |
| 2 | AIDiffDialog.xaml.cs | 删除 `_actionStripSlots` 字段 |
| 3 | AIDiffDialog.xaml.cs | 重写 `PopulateActionStrip()`：构建 FlowDocument |
| 4 | AIDiffDialog.xaml.cs | 新增 `PlaceButtonInActionStrip()` |
| 5 | AIDiffDialog.xaml.cs | 新增 `SyncActionStripScroll()` |
| 6 | AIDiffDialog.xaml.cs | 删除 `CreateActionSlot()` |
| 7 | AIDiffDialog.xaml.cs | 修改 `OnScrollChanged()`：操作条同步改用 `FindScrollViewer(ActionStripViewer)` |
| 8 | AIDiffDialog.xaml.cs | 修改 `SyncHunk()`：同步插入操作条段落 |
| 9 | AIDiffDialog.xaml.cs | 修改 `UndoHunk()`：同步删除操作条段落 |
| 10 | AIDiffDialog.xaml.cs | 修改所有 `ActionStripScroller` 引用 → `FindScrollViewer(ActionStripViewer)` |

## 五、为什么 V6 能彻底解决

### 数学证明

设右侧面板有 N 个段落，第 i 个段落的实际渲染高度为 h(i)。

**V5（StackPanel）**：
- 操作条第 i 个格子高度 = 18（假设值）
- 累积高度 = 18 × i
- 右侧面板累积高度 = Σ h(j), j=0..i-1
- 偏移 = |18 × i - Σ h(j)| → 随 i 增大而增大

**V6（FlowDocument）**：
- 操作条第 i 个段落高度 = h(i)（同一引擎计算）
- 累积高度 = Σ h(j), j=0..i-1
- 右侧面板累积高度 = Σ h(j), j=0..i-1
- 偏移 = 0 → **恒等式**

### 滚动同步验证

```
右侧面板 ExtentHeight = Σ h(i) = H
操作条 ExtentHeight = Σ h(i) = H  （同一引擎，同一配置）
右侧面板 ViewportHeight = V
操作条 ViewportHeight = V'  （可能略不同，因为宽度不同导致垂直滚动条不同）

绝对偏移复制：actionStrip.Offset = right.Offset
→ 两者显示相同 Y 位置的内容
→ 按钮与对应段落完美对齐
```

## 六、风险与注意事项

### 6.1 RichTextBox 内部 ScrollViewer 查找

`FindScrollViewer(RichTextBox)` 方法已存在于代码中，用于左右面板的滚动同步。对操作条 RichTextBox 使用相同方法即可。

### 6.2 操作条段落插入/删除的一致性

SyncHunk/UndoHunk 修改右侧面板的段落数量时，必须**同步修改操作条 FlowDocument 的段落数量**。否则段落索引会错位。

修改原则：**右侧面板插入/删除 N 个段落时，操作条也插入/删除 N 个段落，且插入/删除位置相同**。

### 6.3 性能

操作条 RichTextBox 的段落内容很简单（空 Run 或 InlineUIContainer），渲染开销远小于右侧面板（含语法高亮、词级差异等）。不会影响性能。

### 6.4 操作条 Paragraph Style

XAML 中操作条 RichTextBox 的 `Paragraph Style` 必须与右侧面板**完全一致**：
- `Margin=0`
- `LineHeight=18`
- `LineStackingStrategy=BlockLineHeight`

否则即使使用 FlowDocument，高度仍可能不一致。

## 七、与 V5 的对比

| 维度 | V5 (StackPanel) | V6 (FlowDocument) |
|------|----------------|-------------------|
| 布局引擎 | StackPanel（独立） | FlowDocument（与右侧面板相同） |
| 高度来源 | 假设值 18px/24px | WPF 引擎计算（实际值） |
| 高度匹配 | ❌ 可能不匹配 | ✅ 天然一致 |
| 滚动同步 | 绝对偏移（受总高度影响） | 绝对偏移（总高度相同） |
| 窗口缩放 | 格子高度固定，不受影响 | 段落高度随引擎变化，两侧同步 |
| 代码量 | ~40 行（PopulateActionStrip + CreateActionSlot） | ~60 行（PopulateActionStrip + PlaceButtonInActionStrip + SyncActionStripScroll） |
| 可维护性 | 低（高度假设需要手动维护） | 高（自动匹配，无需维护） |

## 八、验证清单

- [ ] 编译通过（0 错误）
- [ ] 打开差异比对弹窗 → 按钮初始位置正确
- [ ] 滚动到差异区域 → 按钮跟随段落
- [ ] 点击同步 → 恢复按钮在正确位置
- [ ] 点击多个同步 → 每个恢复按钮位置正确
- [ ] 点击恢复 → 同步按钮回到正确位置
- [ ] 点击"全部同步" → 所有恢复按钮位置正确
- [ ] 窗口缩放 → 按钮位置保持正确
- [ ] 滚动到顶部 → 按钮对齐（V5 的核心失败场景）
- [ ] 滚动到底部 → 按钮对齐
