---
name: error-diagnosis
description: "根据日志报错信息自动定位源码根因并给出修复建议。支持运行时异常(Serilog日志)、编译错误(MSBuild输出)、测试失败(xUnit输出)三种输入。触发: 诊断错误、分析报错、定位bug、查日志、诊断最新日志、error diagnosis、troubleshoot"
---

# 错误自动诊断 Skill

根据日志中的报错信息，自动完成：解析 → 分类 → 定位源码 → 根因分析 → 修复建议。仅给出建议，不自动修改代码。

## 输入方式

- **A) 手动粘贴**: 用户粘贴错误日志文本内容
- **B) IDE 引导**: 用户在 SSMS 中点击"诊断最新日志"后，在 CodeBuddy 中说「诊断最新日志」— Skill 自动读取 `diagnosis/temp-latest-errors.txt`
- **C) 粘贴文件路径**: 用户粘贴日志文件路径（如 `D:\Extensions\SSMSBoostPlus\Logs\ssmsboostplus-2026060716.log` 或相对路径 `AIDoc\诊断报告\error.log`）— Skill 自动检测并读取文件内容

## 诊断工作流（8 步）

### Step 0: 输入预处理（路径检测）

**前置条件**: 用户输入不是纯日志文本（无 `[ERR]`、`error CS`、xUnit 标记），而是类似文件路径的字符串。

**检测规则**: 输入同时满足以下两点时，视为文件路径：
1. 以 `.log` 或 `.txt` 结尾（不区分大小写）
2. 包含路径分隔符 `\` 或 `/`，或为绝对路径（如 `D:\`、`F:/`）或项目相对路径

**处理流程**:

```
检测为文件路径？
├─ 是 → 使用 read_file 读取文件内容
│        ├─ 读取成功 → 将内容作为诊断输入，继续 Step 1
│        └─ 读取失败（文件不存在/权限拒绝）
│            → 提示用户：「文件不存在或无法读取: {路径}
│               请确认路径是否正确，或直接粘贴日志文本内容。」
│               等待用户重新输入
└─ 否 → 直接作为日志文本，跳过 Step 0，继续 Step 1
```

**常见路径示例**:
- `D:\Extensions\SSMSBoostPlus\Logs\ssmsboostplus-2026060716.log` — 绝对路径
- `F:\2.C#_Core\SSMSBoostPlus\AIDoc\诊断报告\latest.log` — 绝对路径
- `Logs\ssmsboostplus-20260607.log` — 项目相对路径
- `AIDoc/诊断报告/compile-output.txt` — 正斜杠路径

**支持多行路径**: 如果用户粘贴了带换行的路径列表（如从文件管理器复制的多个文件），Skill 读取第一行作为路径。

### Step 1: 解析 & 标准化

从输入中提取关键字段。支持三种格式：

| 来源 | 格式特征 | 提取字段 |
|------|----------|----------|
| Serilog 日志 | `{Timestamp} [{Level}] [{Machine}] {Message}{NewLine}{Exception}` | 时间戳、级别、机器名、消息、异常堆栈 |
| MSBuild 编译 | `error CSxxxx: message [file(line)]` | 错误码(CSxxxx)、消息、文件路径、行号 |
| xUnit 测试 | `Failed TestName [time]\nError Message: ...\nStack Trace: ...` | 测试名、失败消息、堆栈 |

> 详细格式参考: `references/log-format.md`

**如果格式无法识别**: 提示用户确认输入类型，列出支持的三种格式。

### Step 2: 分类 & 分流

```
有 CSxxxx 编译码？ → build/ 分支
有 xUnit 失败标记？ → test/ 分支
有 Exception 堆栈？  → runtime/ 分支
多个错误混合？       → 逐个分诊，优先级: build > runtime > test
```

### Step 3: 知识库优先检索

对每个错误，先查 `diagnosis/knowledge-base/{分类}/`。

**匹配规则（三点中两点即命中）:**
1. 异常类型完全相同（如 `NullReferenceException`）
2. 堆栈中出现知识库记录的"关键方法"
3. 错误消息包含知识库记录的"消息关键词"

**优先级**: 三点全中 > ①+② > ①+③ > ②+③

- **命中** → 跳 Step 7，直接输出已知方案
- **未命中** → 继续 Step 5

> 知识库索引见: `references/knowledge-base-index.md`

### Step 4: 源码定位 & 上下文分析

从堆栈第一个项目代码帧（跳过 `System.*`、`Microsoft.*` 框架帧）提取：

1. **类名/方法名** → 使用 `search_content` 在 `src/` 下搜索
2. **行号**（如有）→ 使用 `read_file` 读取上下文（前后 20 行）
3. **分析调用链**、参数来源、异常触发条件

**如果堆栈中无项目代码帧**: 标记为"第三方/框架异常"，仅基于消息分析，不做源码定位。

**诊断限制**: 最多 10 次代码搜索/读取操作。超过则停止，标记为"需人工分析"。

### Step 5: 根因推理

综合异常类型 + 代码逻辑 + 项目架构约束推理：

| 场景 | 推理方向 |
|------|----------|
| 空引用 (`NullReferenceException`) | 谁应负责初始化？是否漏了 Null 检查？是否依赖注入未配置？ |
| 反射失败 (`TargetInvocationException` 等) | 类型名/方法名是否正确？程序集是否加载？BindingFlags 是否正确？ |
| 编译错误 (CSxxxx) | 缺少 using？缺少项目引用？类型名拼写错误？版本不匹配？ |
| 测试失败 | 断言值来源是否正确？Mock 设置 (Setup/Returns) 是否遗漏？ |

**项目架构约束（必须遵守）:**
- 依赖链单向: Shared ← Core ← AI ← SSMS，禁止反向引用
- 使用 `ILoggerService` 接口（`SerilogLoggerService` 实现）
- 编译命令: `dotnet build src/SSMS/SSMSBoostPlus.SSMS.csproj -c Release`
- 配置路径: `Assembly.GetExecutingAssembly().Location` 所在目录

### Step 6: 生成修复建议

结构化输出 5 个部分：

1. **问题摘要** — 一句话描述
2. **根因分析** — 为什么发生（引用具体代码位置）
3. **修复方案** — 具体代码改动建议（含 before/after 对比，标注文件路径和行号）
4. **预防措施** — 如何避免再现
5. **关联知识库条目** — 如有匹配，引用条目路径

### Step 7: 输出 & 归档

**对话输出（摘要卡片）:**
```
## 🔍 诊断结果: [错误摘要]

**根因**: [一句话原因]
**位置**: `src/xxx/xxx.cs:NN`
**修复**: [一句话修复方向]

💡 详细报告已保存到 `AIDoc/诊断报告/YYMMDD-HHMMSS-xxx.md`
```

**报告落盘**: 写入 `AIDoc/诊断报告/YYMMDD-HHMMSS-简短摘要.md`，使用以下模板：

```markdown
# 诊断报告: [错误摘要]

| 属性 | 值 |
|------|-----|
| 时间 | YYYY-MM-DD HH:MM:SS |
| 来源 | SSMS 日志 / 编译输出 / 测试运行 |
| 分类 | 运行时异常 / 编译错误 / 测试失败 |
| 严重度 | 🔴 阻断 / 🟡 警告 / 🟢 信息 |
| 知识库 | 命中/未命中 |

## 原始日志
[粘贴原始错误日志]

## 根因分析
[从代码逻辑角度解释]

### 涉及源码
| 文件 | 行号 | 角色 |
|------|------|------|

## 修复建议
### 方案一（推荐）
```csharp
// 文件: xxx, 第 N 行
// 修改前:
[原代码]
// 修改后:
[新代码]
```
### 方案二（备选）
[备选方案]

## 预防措施
- [ ] 具体可执行措施

## 诊断信息
- 诊断耗时: ~Xs
- 代码搜索次数: N
```

**知识归档**: 如果是新错误类型（Step 4 未命中），诊断完成后创建知识库条目：
- 路径: `diagnosis/knowledge-base/{分类}/{异常类型简称}.md`
- 使用 `diagnosis/knowledge-base/_template.md` 模板
- 更新 `references/knowledge-base-index.md` 索引

## IDE 集成说明

当用户在 SSMS 中点击"诊断最新日志"时：
1. DiagnosticCommand 扫描 `{部署目录}\Logs\` 下最新日志
2. 提取 `[ERR]` / `[FTL]` 级别条目
3. 写入 `diagnosis/temp-latest-errors.txt`
4. 弹出提示引导用户在 CodeBuddy 中说「诊断最新日志」
5. Skill 读取临时文件开始诊断

## 日志格式详情

见 `references/log-format.md`