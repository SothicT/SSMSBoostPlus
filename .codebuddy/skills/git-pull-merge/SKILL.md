---
name: git-pull-merge
description: "拉取远程最新代码并合并冲突。标准流程：stash → pull → stash pop → 冲突处理 → 编译验证。触发: 拉取代码、拉取最新代码、pull、合并冲突、同步代码"
---

# SSMSBoostPlus Git 拉取/合并 Skill

自动化 Git 拉取流程：暂存本地改动 → 拉取远程 → 恢复暂存 → 处理冲突 → 编译验证。

## 触发方式

| 触发词 | 行为 |
|--------|------|
| `拉取代码` / `拉取最新代码` / `pull` | stash → pull → stash pop → 冲突处理 → 编译验证 |
| `合并冲突` / `同步代码` | 同上（侧重冲突解决） |

---

## Step 1: 前置检查

### 1.1 检查当前分支

```powershell
git branch --show-current
```

确认当前分支名称，展示给用户知晓。

### 1.2 检查本地变更状态

```powershell
git status --short
git diff --stat
git diff --cached --stat
```

| 状态 | 行为 |
|------|------|
| 工作区干净（无变更） | 跳过 stash，直接进入 Step 3 |
| 有未提交变更 | 进入 Step 2（stash 保护） |

### 1.3 检查远程是否有更新

```powershell
git fetch origin
git log HEAD..origin/<当前分支> --oneline
```

| 结果 | 行为 |
|------|------|
| 无新 commit | 提示"远程无更新，无需拉取"，流程结束 |
| 有新 commit | 展示远程新 commit 列表，继续 Step 2 |

---

## Step 2: Stash 保护本地改动

> 仅当 Step 1.2 检测到有未提交变更时执行

```powershell
git stash push -m "auto-stash-before-pull-<当前分支>-<日期>"
```

- stash message 中包含分支名和日期，便于识别
- 记录 stash 的 hash，用于回滚

```powershell
git stash list          # 确认 stash 成功，记录索引号
```

---

## Step 3: 拉取远程代码

### 策略：默认 merge（保留分支节点）

```powershell
git pull origin <当前分支>
```

> **不使用 `--rebase`**：保留分支历史节点，避免 rebase 重写本地 commit 历史。

| 结果 | 行为 |
|------|------|
| Fast-forward（无冲突） | ✅ 直接进入 Step 4 |
| Auto-merge 成功 | ✅ 展示合并摘要，进入 Step 4 |
| 合并冲突 | ❌ 进入 Step 5（冲突处理） |
| 网络/权限错误 | ❌ 进入回滚流程（Step 7） |

---

## Step 4: 恢复暂存（Stash Pop）

> 仅当 Step 2 执行了 stash 时才需要

```powershell
git stash pop
```

| 结果 | 行为 |
|------|------|
| 无冲突 | ✅ 进入 Step 6（编译验证） |
| Stash pop 冲突 | ❌ 进入 Step 5（冲突处理） |

---

## Step 5: 冲突处理

### 5.1 识别冲突文件

```powershell
git diff --name-only --diff-filter=U
```

列出所有冲突文件，展示给用户。

### 5.2 逐文件处理

对每个冲突文件，展示冲突内容摘要：

```powershell
# 展示冲突区域（标记行及前后 5 行上下文）
git diff <冲突文件> | Select-String -Pattern "^<<<<<<<|^=======|^>>>>>>>" -Context 5,5
```

为每个冲突文件提供三种选择：

| 选项 | 命令 | 说明 |
|------|------|------|
| **保留本地** | `git checkout --ours <文件>` | 使用本地版本覆盖 |
| **保留远程** | `git checkout --theirs <文件>` | 使用远程版本覆盖 |
| **手动合并** | 由 AI 辅助合并 | AI 读取冲突标记，智能合并后写入文件 |

> **推荐**：大多数情况下选择"手动合并"（AI 辅助），因为本地和远程的改动通常都需要保留。

### 5.3 AI 辅助合并策略

对于选择"手动合并"的文件：

1. 读取文件冲突区域内容
2. 分析冲突原因（本地改动 vs 远程改动）
3. 智能合并：保留双方有效改动，去除冲突标记
4. 写入合并后的文件内容
5. 标记为已解决：`git add <文件>`

### 5.4 确认所有冲突已解决

```powershell
git diff --name-only --diff-filter=U
```

| 结果 | 行为 |
|------|------|
| 无冲突文件 | ✅ 进入 Step 6 |
| 仍有冲突 | ❌ 回到 5.2 继续处理 |

---

## Step 6: 编译验证

### 判断是否需要编译

检查恢复后的变更是否涉及代码文件：

```powershell
git status --short
```

| 变更类型 | 是否需要编译 |
|----------|-------------|
| 仅文档（`AIDoc/`、`docs/`、`diagnosis/`） | ❌ 跳过 |
| 仅配置（`.codebuddy/`、`.gitignore`） | ❌ 跳过 |
| 任何 `*.cs`、`*.xaml`、`*.csproj` | ✅ 必须编译 |

### 编译命令

```powershell
dotnet build SSMSBoostPlus.slnx -c Release --nologo -v q 2>&1 | Select-String -Pattern "error|warning|Build succeeded"
```

| 结果 | 行为 |
|------|------|
| `Build succeeded` | ✅ 流程完成，展示最终状态摘要 |
| 编译失败 | ❌ 展示错误，提示修复后重试编译 |

### 测试验证（可选）

若编译成功且用户要求，运行测试：

```powershell
dotnet test tests/SSMSBoostPlus.Tests/SSMSBoostPlus.Tests.csproj -c Release --no-build
```

---

## Step 7: 回滚机制

任何步骤失败时，按以下顺序回滚：

### 7.1 pull 合并失败回滚

```powershell
git merge --abort    # 撤销合并
git stash pop        # 恢复本地改动（如果之前 stash 了）
```

### 7.2 stash pop 冲突回滚（极端情况）

若 stash pop 产生严重冲突且无法解决：

```powershell
# 方案 A：放弃 stash pop，保留 pull 结果
git checkout --ours .           # 保留本地版本
git stash drop                  # 放弃该 stash（已通过 checkout 恢复）

# 方案 B：回滚 pull，恢复原状
git merge --abort               # 撤销合并
git stash pop                   # 恢复本地改动
```

> **原则**：优先保证本地改动不丢失。宁可回滚 pull，也不丢失 stash。

---

## 最终状态摘要

流程完成后，展示摘要：

```
📋 拉取合并完成

  分支：feat-ai
  远程新 commit：3 个
  合并方式：merge (auto-merge)
  冲突文件：2 个（已解决）
  编译状态：✅ Build succeeded
  本地改动：已恢复
```

---

## 边缘情况处理

| 场景 | 处理 |
|------|------|
| 工作区干净，无本地改动 | 跳过 stash/pop，直接 pull |
| 远程无更新 | 提示"无需拉取"，流程结束 |
| pull 产生 merge commit | 正常，保留 merge commit |
| stash 多次（已有旧 stash） | 新 stash 不会覆盖旧 stash，按索引操作 |
| 所有冲突选择同一策略 | 批量处理，逐文件确认 |
| 编译失败但合并成功 | 提示修复代码，不回滚合并 |
| 当前分支不存在于远程 | 提示"远程无此分支"，流程中止 |

---

## 行为约束

- **不使用** `git pull --rebase`（用户选择 merge 策略）
- **不使用** `--force`、`--hard`、`--no-verify`
- **不修改** `.gitignore`、`git config`
- **不丢弃** stash（除非用户明确确认）
- **不自动 push**（拉取合并后不推送，推送需使用 git-commit-push Skill）
- 仅操作当前项目仓库，不跨仓库
- 冲突文件处理前必须展示内容，不盲目选择 ours/theirs