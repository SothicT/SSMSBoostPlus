---
title: 差异块按 ChangeType 分块精细化同步方案
date: 2026-07-13
author: AI Assistant
tags: [AIDiffDialog, diff, hunk, sync, ChangeType]
---

# 差异块按 ChangeType 分块精细化同步方案

## 需求背景

当前比对弹窗中，`BuildDiffHunks` 将**所有连续的差异行**合并为一个 hunk，无论这些行是新增（Inserted）、删除（Deleted）还是冲突（Modified）。整个 hunk 只有一个同步按钮（▶），点击后按 `SyncHunk` 逻辑整体处理。

用户期望：当一个 hunk 内部包含多种 ChangeType 时，**按类型分别分块**，每种类型的连续行作为一个独立块，各自有独立的同步按钮：

```
当前大块（1个按钮）：
  ┌─ 新增行1  ─┐
  ├─ 新增行2  ┤── 一个同步按钮，同时处理全部
  ├─ 冲突行1  ┤
  └─ 冲突行2  ┘

期望分块（3个按钮）：
  ┌─ 新增行1  ─┐── 新增块：添加按钮（+）
  └─ 新增行2  ┘
  ┌─ 冲突行1  ─┐── 冲突块：替换按钮（▶）
  └─ 冲突行2  ┘
  ┌─ 删除行1  ─┐── 删除块：删除按钮（✕）
  └─ 删除行2  ┘
```

---

## 技术分析

### 当前 hunk 划分逻辑

文件：`AIDiffDialog.xaml.cs`

**`BuildDiffHunks`**（第 2344-2393 行）：遍历左右 DiffPiece 行，将所有 `ChangeType != Unchanged` 的连续行合并为一个 `DiffHunkInfo`。

**`RebuildDiffHunksFromState`**（第 2269-2304 行）：重建时只根据 `_rightIndicatorBackgrounds` 判断是否是差异（非 `LineNumberBgDefault`/`SyncedBackground`/`ResolvedBackground`），同样合并连续差异行为一个 hunk。

**`DiffHunkInfo` 类**（第 155-159 行）：只有 `BlockStartIndex` 和 `BlockEndIndex`，没有 `ChangeType` 字段。

### 关键数据结构

- **`_diffHunks`** `List<DiffHunkInfo>` — 所有当前显示的差异块
- **`_hunkInsertedBlocks`** `Dictionary<int, (List<int> Inserted, int OrigStart, int OrigEnd)>` — 已同步的 hunk 状态
- **`_rightIndicatorBackgrounds`** `Dictionary<int, Brush>` — 每行的背景色（用于判断差异状态）

### 影响范围

分块变更涉及：

| 组件 | 影响 |
|------|------|
| `DiffHunkInfo` 类 | 需添加 ChangeType 字段 |
| `BuildDiffHunks` | 按 ChangeType 分裂 hunk |
| `BuildHunkDiffHunks` | Hunk-Only 模式同样需要分裂 |
| `RebuildDiffHunksFromState` | 按背景色+原始 ChangeType 分裂 |
| `PopulateActionStrip` | 为每种类型创建不同按钮 |
| `SyncHunk` | 按类型执行不同同步逻辑 |
| `CreateSyncButton` | 按钮图标/文字需区分类型 |
| `PositionActionButtons` | 定位逻辑不变（基于 blockIndex） |
| hunk 导航（Prev/Next） | 导航步数变多，但逻辑不变 |

---

## 方案设计

### 1. 扩展 DiffHunkInfo

```csharp
private class DiffHunkInfo
{
    public int BlockStartIndex { get; set; }
    public int BlockEndIndex { get; set; }
    
    /// <summary>
    /// 差异类型：
    ///   Inserted — 新增（右侧独有）
    ///   Deleted — 删除（左侧独有）
    ///   Modified — 冲突修改（两侧都有但不同）
    /// </summary>
    public ChangeType HunkChangeType { get; set; }
}
```

### 2. 判断每行属于哪种 ChangeType

对于左右 Type 的组合，判断逻辑：

```csharp
private static ChangeType ClassifyDiffLine(ChangeType leftType, ChangeType rightType)
{
    // 新增：右侧有内容，左侧没有
    if (rightType == ChangeType.Inserted 
        || (leftType == ChangeType.Imaginary && rightType != ChangeType.Imaginary)
        || (leftType == ChangeType.Unchanged && rightType == ChangeType.Modified))
        return ChangeType.Inserted;
    
    // 删除：左侧有内容，右侧没有
    if (leftType == ChangeType.Deleted
        || (leftType != ChangeType.Imaginary && rightType == ChangeType.Imaginary)
        || (leftType == ChangeType.Modified && rightType == ChangeType.Unchanged))
        return ChangeType.Deleted;
    
    // 冲突：两侧都有但不同
    if (leftType == ChangeType.Modified && rightType == ChangeType.Modified)
        return ChangeType.Modified;
    if (leftType == ChangeType.Deleted && rightType == ChangeType.Inserted)
        return ChangeType.Modified;
    
    return ChangeType.Unchanged;
}
```

### 3. 修改 BuildDiffHunks

在检测到连续差异行时，进一步按 `ClassifyDiffLine(leftType, rightType)` 分裂：

```csharp
private void BuildDiffHunks(SideBySideDiffModel sideBySide)
{
    _diffHunks.Clear();
    var leftLines = sideBySide.OldText.Lines;
    var rightLines = sideBySide.NewText.Lines;
    if (leftLines == null || rightLines == null) return;

    var lineCount = Math.Max(leftLines.Count, rightLines.Count);
    int subHunkStart = -1;
    ChangeType currentType = ChangeType.Unchanged;

    for (int i = 0; i < lineCount; i++)
    {
        var leftType = i < leftLines.Count ? leftLines[i].Type : ChangeType.Unchanged;
        var rightType = i < rightLines.Count ? rightLines[i].Type : ChangeType.Unchanged;
        bool isDiff = leftType != ChangeType.Unchanged || rightType != ChangeType.Unchanged;

        if (isDiff)
        {
            var lineType = ClassifyDiffLine(leftType, rightType);
            
            if (subHunkStart < 0)
            {
                // 第一个差异行
                subHunkStart = i;
                currentType = lineType;
            }
            else if (lineType != currentType)
            {
                // 类型变化 → 结束当前子块，开始新子块
                _diffHunks.Add(new DiffHunkInfo
                {
                    BlockStartIndex = subHunkStart,
                    BlockEndIndex = i - 1,
                    HunkChangeType = currentType
                });
                subHunkStart = i;
                currentType = lineType;
            }
        }
        else
        {
            if (subHunkStart >= 0)
            {
                _diffHunks.Add(new DiffHunkInfo
                {
                    BlockStartIndex = subHunkStart,
                    BlockEndIndex = i - 1,
                    HunkChangeType = currentType
                });
                subHunkStart = -1;
            }
        }
    }

    if (subHunkStart >= 0)
    {
        _diffHunks.Add(new DiffHunkInfo
        {
            BlockStartIndex = subHunkStart,
            BlockEndIndex = lineCount - 1,
            HunkChangeType = currentType
        });
    }
}
```

### 4. 修改 RebuildDiffHunksFromState

重建时需要知道每行原始的 ChangeType。当前 `_rightIndicatorBackgrounds` 只保存背景色，不保存 ChangeType。需要新增 `_lineChangeTypes` 字典：

```csharp
private readonly Dictionary<int, ChangeType> _lineChangeTypes = new();
```

在 `BuildParagraph` / `BuildHunkFilteredDocument` 中填充此字典。

然后在 `RebuildDiffHunksFromState` 中使用它来分裂 hunk。

### 5. 创建不同类型的同步按钮

| 类型 | 按钮图标 | 按钮颜色 | 提示文字 |
|------|---------|---------|---------|
| Inserted（新增） | `+` | 绿色 `#4CAF50` | "从数据库添加此行" |
| Deleted（删除） | `✕` | 红色 `#F44336` | "从数据库删除此行" |
| Modified（冲突） | `▶` | 蓝色 `#0078D4` | "采用数据库版本"（保留现有） |

```csharp
private Button CreateSyncButton(int hunkIndex, ChangeType changeType)
{
    string content, tooltip;
    Brush bg;
    
    switch (changeType)
    {
        case ChangeType.Inserted:
            content = "+";
            bg = new SolidColorBrush(Color.FromRgb(76, 175, 80));
            tooltip = "从数据库添加此行";
            break;
        case ChangeType.Deleted:
            content = "✕";
            bg = new SolidColorBrush(Color.FromRgb(244, 67, 54));
            tooltip = "从数据库删除此行";
            break;
        default: // Modified
            content = "▶";
            bg = SyncButtonBackground;
            tooltip = "采用数据库版本";
            break;
    }
    
    var button = new Button { Content = content, ... Background = bg, ToolTip = tooltip };
    button.Click += SyncBlock_Click;
    return button;
}
```

### 6. 修改 SyncHunk 按类型处理

不同 ChangeType 的同步逻辑：

| 类型 | 同步逻辑 |
|------|---------|
| Inserted | 在本地插入 DB 的代码行（右侧已显示，仅标记"新增已确认"） |
| Deleted | 标记右侧对应行为"待删除"（添加删除线，如同步删除分支） |
| Modified | 插入 DB 代码到上方，标记原行为已解决（现有逻辑） |

---

## 修改点汇总

| # | 文件 | 修改内容 | 复杂度 |
|---|------|---------|--------|
| 1 | `AIDiffDialog.xaml.cs` | `DiffHunkInfo` 添加 `HunkChangeType` 字段 | 低 |
| 2 | `AIDiffDialog.xaml.cs` | 新增 `ClassifyDiffLine` 方法 | 低 |
| 3 | `AIDiffDialog.xaml.cs` | 新增 `_lineChangeTypes` 字典 | 低 |
| 4 | `AIDiffDialog.xaml.cs` | 修改 `BuildDiffHunks` 按类型分裂 | 中 |
| 5 | `AIDiffDialog.xaml.cs` | 修改 `BuildHunkDiffHunks` 按类型分裂 | 中 |
| 6 | `AIDiffDialog.xaml.cs` | 修改 `RebuildDiffHunksFromState` 按类型分裂 | 中 |
| 7 | `AIDiffDialog.xaml.cs` | 修改 `CreateSyncButton` 接受 ChangeType 参数 | 低 |
| 8 | `AIDiffDialog.xaml.cs` | 修改 `PopulateActionStrip` 传递 ChangeType | 低 |
| 9 | `AIDiffDialog.xaml.cs` | 修改 `SyncHunk` 按 ChangeType 分支处理 | 中 |
| 10 | `AIDiffDialog.xaml.cs` | 修改 `BuildParagraph` 填充 `_lineChangeTypes` | 低 |

---

## 注意事项

1. **向后兼容**：现有 `_hunkInsertedBlocks` 结构和同步/撤销逻辑需要适配 ChangeType
2. **Hunk-Only 模式**：`BuildHunkDiffHunks` 在 Hunk-Only 模式下也需要分裂
3. **全部同步**：`SyncAll_Click` 遍历所有 hunk 逐个同步，逻辑不变
4. **撤销同步**：`UndoHunk` 逻辑不变（基于 `_hunkInsertedBlocks` 状态）
5. **导航**：`PrevDiff_Click` / `NextDiff_Click` 基于 `_diffHunks` 索引导航，步数变多但逻辑不变

---

## 测试建议

1. **混合类型 hunk**：创建包含新增+冲突+删除的差异场景，验证每种类型有独立按钮
2. **单一类型 hunk**：仅新增/仅删除/仅冲突，验证按钮类型正确
3. **部分同步**：仅同步新增行，验证冲突和删除行不受影响
4. **全部同步**：逐个同步所有子块，验证最终结果正确
5. **Hunk-Only 模式**：验证大文件下分裂仍然正确
6. **撤销**：撤销某个子块的同步，验证其他子块不受影响