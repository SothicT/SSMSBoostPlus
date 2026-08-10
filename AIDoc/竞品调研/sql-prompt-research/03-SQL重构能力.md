# 03 - SQL 重构能力

## 一、功能概述

SQL Prompt 的 SQL 重构功能使其在同类工具中具有显著优势，特别是**跨数据库对象依赖自动处理**的能力（Smart Rename、Split Table），这一特性是目前竞品难以匹敌的。

---

## 二、功能细节

### 2.1 重构操作清单

#### 小规模重构（单批次/单对象）

| 重构操作 | 快捷键 | 说明 |
|----------|--------|------|
| **Rename Alias/Variable** | F2 | 仅限当前批次内的精确匹配项，非精确匹配不会被误改 |
| **Expand Wildcards** | `Ctrl+B, Ctrl+W` | 将 `SELECT *` 展开为具体列清单 |
| **Qualify Object Names** | `Ctrl+B, Ctrl+Q` | 为对象名添加所有者前缀 |
| **Insert Semicolons** | `Ctrl+B, Ctrl+C` | 自动添加缺失的分号 |
| **Add/Remove Brackets** | `Ctrl+B, Ctrl+B` | 统一添加或移除方括号 |
| **Inline Stored Procedure** | `Ctrl+B, Ctrl+I` | 将 EXEC 调用展开为内联代码 |
| **Encapsulate as new SP** | `Ctrl+B, Ctrl+E` | 将选中 SQL 封装为新存储过程 |
| **Surround With** | `Ctrl+K, Ctrl+S` | 用 BEGIN...END / WHILE / IF 包裹代码 |

#### 代码片段重构（通过 `$SELECTEDTEXT$` 占位符）

| 片段 | 功能 |
|------|------|
| `be` | 包裹为 BEGIN...END 块 |
| `ifs` | 包裹为 IF 块 |
| `tc` | 包裹为 TRY...CATCH 块 |
| `cte` | 转为 CTE 表达式 |
| `cv` | 创建视图 |
| `citf` | 创建内联表值函数 |

### 2.2 Smart Rename（智能重命名）

支持重命名的对象类型：表（含列）、视图（含列）、存储过程（含参数）、函数（含参数）。

**安全性保证机制**：

| 机制 | 说明 |
|------|------|
| 依赖自动更新 | 自动修改所有引用该对象的相关对象 |
| 操作预览 | 显示 Actions（操作摘要）、Warnings（警告）、Dependencies（依赖对象） |
| 脚本生成而非直接执行 | 生成完整 SQL 脚本供审查，需手动执行 |
| 权限和扩展属性保留 | 原对象的权限和扩展属性自动保留 |
| 事务保护 | 所有操作包装在单个事务中 |
| 撤销支持 | 支持 Ctrl+Z 回退 |

**已知限制**：
- 临时表：需先关闭系统版本控制
- 行级安全性：需手动处理安全策略和谓词函数
- 架构绑定对象：需先删除架构绑定

### 2.3 Split Table（拆分表）

将一个表拆分为两个关联表，生成完整脚本。

```
原表: Orders (OrderID, CustomerID, CustomerName, Address, ProductID, Amount)
               ↓ Split Table
新表1: Orders (OrderID, CustomerID, ProductID, Amount)
新表2: Customers (CustomerID, CustomerName, Address)
       FK: Orders.CustomerID → Customers.CustomerID
```

**处理流程**：
1. 选择要迁移到新表的列
2. 复制主键作为关联键
3. 建立 FOREIGN KEY 约束
4. 自动修改所有依赖对象（视图、存储过程、函数）
5. 原有单表查询自动改写为 INNER JOIN

---

## 三、技术原理推测

### 3.1 Smart Rename 的核心机制

```
┌──────────────────────────────────────────────┐
│           Smart Rename 流程                    │
├──────────────────────────────────────────────┤
│ 1. 解析目标对象的所有依赖                       │
│    - 查询 sys.sql_expression_dependencies     │
│    - 解析所有引用该对象的存储过程/函数/视图      │
│    - 构建完整的依赖图                          │
│                                               │
│ 2. 生成变更脚本                                │
│    - 新增对象（新的 create 语句）               │
│    - 修改所有依赖对象                          │
│    - 删除旧对象                                │
│                                               │
│ 3. 预览 + 审查                                │
│    - 展示影响范围                              │
│    - 用户确认后执行                             │
│                                               │
│ 4. 事务执行                                   │
│    - BEGIN TRANSACTION                       │
│    - 执行所有变更                              │
│    - 成功 → COMMIT / 失败 → ROLLBACK          │
└──────────────────────────────────────────────┘
```

### 3.2 依赖分析的技术挑战

| 挑战 | 说明 | 解决方式 |
|------|------|---------|
| 动态 SQL | 存储过程内的 `EXEC(@sql)` 无法静态分析 | 提示用户手动检查 |
| 循环依赖 | A 依赖 B 同时 B 依赖 A | 检测并报告，提示手动处理 |
| 跨数据库引用 | 对象引用其他数据库的对象 | 需要连接对应数据库 |
| 延迟名称解析 | 存储过程中引用尚未创建的对象 | 标记为潜在问题 |

### 3.3 Split Table 的实现要点

1. **列迁移**：从原表中选择列 → 创建新表 → 数据迁移
2. **键关系**：复制主键 → 创建 FK 约束
3. **查询重写**：使用正则或 AST 转换，将单表查询改写为 JOIN 查询
4. **事务保护**：全部操作包裹在事务中

---

## 四、用户体验亮点

1. **预览优先**：所有重构操作先生成脚本预览，不直接修改数据库
2. **事务保护**：Smart Rename 和 Split Table 均使用事务包裹
3. **依赖可见**：清晰展示重构将影响的依赖对象
4. **撤销能力**：支持 Ctrl+Z 回退

---

## 五、可实现的借鉴方案

### 5.1 技术路线

| 重构类型 | 复杂度 | 技术方案 |
|----------|--------|---------|
| **基础重构** | 低 | AST 操作 + 文本替换 |
| Expand Wildcards | 低 | 查询 INFORMATION_SCHEMA.COLUMNS + 文本替换 |
| Qualify Object Names | 低 | 规则匹配 + 文本替换 |
| Insert Semicolons | 低 | 正则匹配 + 插入 |
| **中级重构** | 中 | 依赖查询 + 脚本生成 |
| Inline SP | 中 | 解析存储过程体 + 参数展开 |
| Surround With | 中 | 代码模板 + 缩进调整 |
| **高级重构** | 高 | 全依赖分析 + 事务脚本生成 |
| Smart Rename | 高 | sys.sql_expression_dependencies + 脚本生成 |
| Split Table | 极高 | 列分析 + 约束生成 + 查询重写 |

### 5.2 关键挑战

| 挑战 | 应对策略 |
|------|---------|
| **依赖完整性** | 使用 `sys.sql_expression_dependencies` 作为主数据源，但需注意其局限性 |
| **动态 SQL** | 对于无法分析的动态 SQL，给出明确警告提示 |
| **错误恢复** | 事务保护 + 操作前快照（SQL Compare 思想） |
| **查询重写准确率** | 对于 Split Table 的查询重写，先支持简单场景再逐步覆盖 |

### 5.3 最小可行实现

```
阶段1（基础）：文本级重构
  - Expand Wildcards
  - Qualify Object Names
  - Insert Semicolons
  - Add/Remove Brackets

阶段2（中级）：代码结构重构
  - Inline Stored Procedure
  - Encapsulate as new SP
  - Surround With（代码片段包裹）

阶段3（高级）：数据库级重构
  - Smart Rename（先从简单对象开始）
  - Split Table（高复杂度，谨慎推进）
```

---

## 六、优先级建议

| 评估维度 | 评分 | 说明 |
|----------|------|------|
| 实现难度 | **极高** (5/5) | Smart Rename 需要完整的依赖分析 |
| 业务价值 | **中** (3/5) | 使用频率低于补全和格式化，但质量要求高 |
| 实现优先级 | **P2** | 先完成补全和格式化，再逐步实现重构功能 |