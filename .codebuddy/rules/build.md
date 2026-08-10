---
description: 
alwaysApply: true
enabled: true
updatedAt: 2026-06-09T09:36:50.755Z
provider: 
---

# 构建与编译规则

## 编译命令

```powershell
# ✅ 正确：使用 .slnx（.sln 被企业加密软件加壳，dotnet 无法解析）
dotnet build SSMSBoostPlus.slnx -c Release

# ❌ 禁止：不要使用 .sln
dotnet build SSMSBoostPlus.sln -c Release
```

## 项目结构

| 项目 | 目标框架 | 特殊说明 |
|------|----------|----------|
| Shared | netstandard2.0 | 纯共享库，无外部依赖 |
| Core | netstandard2.0 | 核心功能层 |
| AI | netstandard2.0 | AI 服务封装 |
| SSMS | net472 | SSMS 扩展插件宿主 |
| Tests | net8.0 | xUnit 测试项目 |

## PkgDef 生成机制

**重要**：`PkgDef` 手动生成是**永久方案**，不要尝试"修复"回 VSSDK 原生方式。

原因：.NET SDK 10 + VSSDK 17.14 的 `PrepareForRunDependsOn` 存在覆盖 bug，已通过 `ManualPkgDef.targets` 的 `ManualPkgDef` AfterTargets 绕过。

```xml
<!-- 禁止修改此机制，如遇问题请联系项目维护者 -->
<Import Project="$(MSBuildExtensionsPath)\ManualPkgDef.targets" />
```

## NuGet 配置

内部 HTTP NuGet 源必须设置 `allowInsecureConnections="true"`：

```xml
<!-- nuget.config 已正确配置，无需修改 -->
<packageSourceCredentials>
  <feed key="...">
    <add key="Username" value="..." />
    <add key="ClearTextPassword" value="..." />
  </feed>
</packageSourceCredentials>
```

## SSMS 内部 DLL 引用规则

| DLL | 编译时引用 | 运行时加载 |
|-----|-----------|-----------|
| envdte.dll | ✅ 是（Private=False） | - |
| VSIntegration*.dll | ❌ 否 | ✅ 动态加载 |
| ObjectExplorer*.dll | ❌ 否 | ✅ 动态加载 |

SSMS 项目中对 VSIntegration、ObjectExplorer 的引用：
- **编译时**：不直接引用，通过反射/DTE 动态获取
- **运行时**：由 SSMS 宿主加载，确保类型存在