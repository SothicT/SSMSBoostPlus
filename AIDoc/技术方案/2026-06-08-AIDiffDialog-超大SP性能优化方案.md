# AIDiffDialog 超大 SP 性能优化方案

> 日期：2026-06-08 | 状态：方案讨论中

## 背景

用户在 VersionGuard 中比对超大存储过程（1000-5000 行）时，AIDiffDialog 出现严重卡顿。

## 现象

| 环节 | 表现 |
|------|------|
| 窗口弹出 | 点击「查看差异」后 >3 秒才显示 |
| 滚动 | 左右面板滚动时掉帧、不跟手 |
| 导航 | 点击「上一个/下一个差异」有明显延迟 |
| 触发条件 | 总行数大（1000-5000），差异行数可能不多 |

## 根因分析

### 架构链路

```
VersionWarningDialog.ShowDiffForAsync()
  → ObjectExtractor.ExtractObjectScript()   ← 本地脚本提取
  → DiffCalculator.GetSideBySideDiff()      ← DiffPlex Myers O(N×D)
  → AIDiffDialog 构造函数
       ├─ BuildSideDocument(OldText, isLeft)  → 5000 Paragraph
       └─ BuildSideDocument(NewText, isRight) → 5000 Paragraph
            └─ 每个 Paragraph 含 3-8 个 Inline（行号 + 文本 + SubPieces + ▶Button）
```

### 核心瓶颈：RichTextBox FlowDocument 无虚拟化

```
5000 行 × 2 面板 = 10000+ Paragraph
每个 Paragraph ≈ 3-8 个 Inline 元素
总计：10000+ Paragraph + 30000-50000+ Inline + 若干 FrameworkElement(▶Button)

↓ 全部在构造函数中同步创建 → UI 线程阻塞 → 窗口弹不出
↓ 全部常驻内存 → 每次滚动/导航触发整个 FlowDocument 重排 → 持续卡顿
```

### 各阶段瓶颈

| 阶段 | 卡顿原因 |
|------|----------|
| 窗口弹出慢 | `BuildSideDocument()` 同步创建 10000+ Paragraph + 数万 Inline，全部在 UI 线程 |
| 滚动掉帧 | RichTextBox 无虚拟化，10000 个 Paragraph 全部参与布局计算 |
| 导航卡顿 | `NavigateToDiff()` 中 `ResetParagraphBackgrounds()` 遍历全部 Paragraph 的背景字典 |

## 候选方案

### 方案 A：Hunk-Only 渲染（推荐）

**思路**：仿 Git diff，仅渲染差异块 + 上下文行。5000 行中只有变更处才是用户关心的。

```
全量模式（当前）:  渲染 5000 行 → 10000 Paragraph → 💥
Hunk-Only 模式:     只改 3 行，上下文各 5 行 → 渲染 ~15 行 → 30 Paragraph → ⚡
```

**改动**：
- `AIDiffDialog.xaml.cs` 新增 `BuildHunkFilteredDocument()` 方法
- 省略号占位行：`... 4970 unchanged lines ...`
- 「展开全部」ToggleButton 可切回全量渲染
- 阈值：总行数 > 500 自动启用 Hunk 模式
- `BuildSideDocument()` 保留不变用于 < 500 行的小文件

**优点**：
- 改动最小，只改一个文件
- 5000 行 → 实际渲染 50-200 行，性能提升 25-100 倍
- ▶ 按钮、词级高亮、导航、同步滚动全保留
- 无新依赖

**缺点**：
- 用户看不到完整文件全貌（可通过「展开全部」缓解）

---

### 方案 B：异步渐进加载

**思路**：窗口先弹出 + 骨架屏，后台构建 FlowDocument，分批刷入。

**改动**：
- 窗口立刻 `Show()`，显示「正在计算差异...」
- `Task.Run` 构建数据，`Dispatcher.Invoke` 分批刷入 Paragraph
- 仅解决弹出慢，滚动卡顿依旧

**优点**：不改渲染逻辑
**缺点**：治标不治本，滚动卡顿依旧

---

### 方案 C：替换 RichTextBox 为虚拟化控件

**思路**：用 `ListView + VirtualizingStackPanel` 或 AvalonEdit。

| 方案 | 工作量 |
|------|--------|
| ListView 自绘行 | 中 — 需重写行模板 |
| AvalonEdit | 低 — 原生虚拟化 + 代码高亮，但引入新依赖 |

**优点**：彻底解决渲染性能
**缺点**：重构量大，▶ 按钮实现方式完全不同

---

### 方案 D：组合拳

```
方案 A（Hunk-Only）+ 方案 B（异步加载）
= 默认只渲染差异块 + 后台构建 + 窗口秒开 + 滚动丝滑
```

## 推荐结论

**首选方案 A（Hunk-Only 渲染）**，理由：

1. 改动最小 — 只改 `AIDiffDialog.xaml.cs`
2. 效果最显著 — 渲染量降低 95%+
3. 无新依赖 — 不引入 AvalonEdit
4. 不牺牲功能 — 所有现有功能保留
5. 语义正确 — 版本比对核心是找差异

> 下一步：待用户确认后进入详细设计和实现阶段。