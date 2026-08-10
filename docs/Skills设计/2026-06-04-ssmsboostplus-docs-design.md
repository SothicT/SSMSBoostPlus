# SSMSBoost++ 文档编写设计稿

**日期**：2026-06-04
**状态**：已批准

## 文档一：用户手册

**路径**：`AIDoc/SSMSBoostPlus-用户手册.md`
**受众**：普通开发人员 + DBA/高级用户
**形式**：Markdown + 截图占位符（`{{Screenshot: ...}}`）

### 目录
1. 插件简介
2. 安装与部署
3. AI 代码助手（4个子功能）
4. 版本守护（4个子功能）
5. 设置
6. 常见问题
7. 附录（快捷键速查 / 配置路径说明）

---

## 文档二：内部开发文档

**路径**：`AIDoc/SSMSBoostPlus-开发文档.md`
**受众**：接手维护的开发人员
**形式**：纯 Markdown，侧重架构 + 原理

### 目录
1. 项目概览
2. 架构设计（5个子节）
3. 核心模块详解（5个小节）
4. 版本守护核心算法（4个小节）
5. AI 桥接层原理（3个小节）
6. SSMS 集成细节（4个小节）
7. 构建与部署（4个小节）
8. 附录（GUID / 已知bug / 调试技巧）

---

## 内容来源
基于源代码分析：
- `src/SSMS/Commands/` 下的 11 个命令处理器
- `src/Core/Models/Configurations.cs` 配置模型
- `src/Core/VersionGuard/VersionChecker.cs` 版本检查器
- `src/Core/AI/` AI 桥接层
