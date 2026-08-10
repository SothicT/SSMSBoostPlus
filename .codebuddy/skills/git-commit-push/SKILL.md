---
name: git-commit-push
description: "按变更分类拆分 Git 提交，自动编译检查，按需推送。支持多级触发：提交=仅commit、提交并推送=commit+push确认、推送=仅push。触发: 提交代码、commit、commit push、提交并推送、推送、push"
---

# SSMSBoostPlus Git 提交/推送 Skill

自动化 Git 提交流程：分析变更 → 编译检查 → 按分类拆分 commit → 询问推送。

## 触发方式（多级触发）

| 触发词 | 行为 |
|--------|------|
| `提交` / `commit` | 分析变更 → 编译检查 → 分类拆分 commit（不 push） |
| `提交并推送` / `commit push` | 同上 + 展示摘要 → 询问确认 → push |
| `推送` / `push` | 直接 push 当前分支已有的未推送 commit |

---

## Step 1: 分析变更

```powershell
git status --short          # 获取所有变更文件列表
git diff --stat             # 未暂存变更
git diff --cached --stat    # 已暂存变更
```

### 分类规则（按目录/文件名自动分组）

| 前缀 | 分类 | 匹配规则 |
|------|------|----------|
| `feat(Chat)` | AI 对话面板 | `src/SSMS/UI/Chat/`、`AIChatCommand`、`IChatSessionService`、`ChatSessionService` |
| `feat(Bridge)` | AI Bridge 增强 | `src/AI/Bridge/` 功能新增/增强 |
| `feat(Core)` | Core 层功能 | `src/Core/` 非接口的新功能文件 |
| `feat(SSMS)` | SSMS 插件功能 | `src/SSMS/` 新功能（非 UI Chat、非 Command 修复） |
| `refactor` | 重构 | 重命名、结构调整、接口适配（同层间改动） |
| `fix(UI)` | UI 修复 | `*.vsct`、`*.xaml` 修复类改动 |
| `fix(Bridge)` | Bridge 修复 | `src/AI/Bridge/` Bug 修复 |
| `fix(Core)` | Core 修复 | `src/Core/` Bug 修复 |
| `chore` | 工程/配置 | `.codebuddy/skills/`、`*.csproj`、`.gitignore`、`nuget.config` |
| `docs` | 文档/诊断 | `AIDoc/`、`diagnosis/`、`docs/` |
| `test` | 测试 | `tests/` |

### 若变更无法自动分类

展示变更列表 → 让用户手动确认分类归属。

---

## Step 2: 编译检查（dotnet build）

### 跳过条件

若本次变更**仅涉及以下分类**，则跳过编译检查，直接进入 Step 3：

| 可跳过分类 | 说明 |
|------------|------|
| `docs` | `AIDoc/`、`diagnosis/`、`docs/` |
| `chore` | `.codebuddy/skills/`、`*.json`（不含 `.csproj`）、`.gitignore` |
| `test` | `tests/`（纯测试文件，无业务代码变更） |

> **判断逻辑**：分析 Step 1 分类结果，若所有分组均属于上表，则跳过编译检查。

### 执行条件

若存在 **任意一个** `feat` / `fix` / `refactor` / `feat(SSMS)` 等代码变更分类，则执行编译检查：

```powershell
dotnet build SSMSBoostPlus.slnx -c Release --nologo -v q 2>&1 | Select-String -Pattern "error|warning|Build succeeded"
```

| 结果 | 行为 |
|------|------|
| `Build succeeded` | ✅ 继续 Step 3 |
| 编译失败 | ❌ 中止，展示错误信息，不提交 |

> **注意**：warning 不阻塞提交，但会展示数量供用户知晓。

---

## Step 3: 按分类逐步提交

对每个有变更的分类分组：

```powershell
git add <该分组文件列表>
git commit -m "<prefix>: <变更说明>"
```

### Commit Message 格式

遵循 [Conventional Commits](https://www.conventionalcommits.org/) 规范：

```
<type>(<scope>): <中文简述>

- 子项1
- 子项2
```

### 示例

```
feat(Chat): AI 对话面板可停靠 ToolWindow + ChatSession 服务

- 新增 AIChatToolWindow (Ctrl+Shift+K)
- 新增 IChatSessionService / ChatSessionService
- 新增 AIChatCommand 入口 + Chat UI 组件
```

```
fix(UI): 菜单快捷键修复，移除 ButtonText 中 \t 文本

- vsct 中移除手动写入的快捷键文本
- VS Studio 自动从 KeyBindings 读取并右对齐显示
```

```
refactor: AI Command 适配 Bridge 接口变更

- AICommentsCommand / AIFormattingCommand 等统一适配
```

---

## Step 4: 推送询问

> 仅 `提交并推送` / `commit push` 触发此步骤

展示所有 commit 摘要后，询问：

```
📋 已提交以下 commit：
  1. feat(Chat): ...
  2. fix(UI): ...
  3. docs: ...

是否推送到 origin/feat-ai？[是/否]
```

| 用户确认 | 行为 |
|----------|------|
| 是 | `git push origin <当前分支>` |
| 否 | 保留本地，提示"可稍后使用 `推送` 命令推送" |

### 推送失败处理

| 错误 | 处理 |
|------|------|
| 远程有更新 | 提示 `git pull --rebase`，不自动合并 |
| 权限/网络 | 展示错误，建议检查凭证 |

---

## 边缘情况处理

| 场景 | 处理 |
|------|------|
| 无变更 | 提示"工作区干净，无需提交" |
| 仅一个分类 | 不拆分，单个 commit |
| 混合修改（同一文件涉及多个分类） | 按文件主要内容归入一个分类，不拆分单文件 |
| 编译检查失败 | 不提交，展示错误，提示修复后重试 |
| push 时无未推送 commit | 提示"所有 commit 已推送" |

---

## 行为约束

- **不修改** `.gitignore`、`git config`
- **不使用** `--force`、`--no-verify`、`--amend`（除非用户明确要求）
- **不做** `git reset --hard`、`git clean`
- 仅操作当前项目仓库，不跨仓库