---
description: 
alwaysApply: true
enabled: true
updatedAt: 2026-06-09T09:37:15.440Z
provider: 
---

# 部署规则

## 部署脚本

| 项目 | 说明 |
|------|------|
| 脚本 | `deploy-to-ssms.bat` |
| **必须** | 以**管理员权限**运行 |

```powershell
# 右键 → 以管理员身份运行
deploy-to-ssms.bat
```

## 目标目录

```
D:\Microsoft SQL Server Management Studio 22\Release\Common7\IDE\Extensions\SSMSBoostPlus
```

**注意**：部署前需先**关闭 SSMS**，否则 DLL 会被锁定。

## 部署清单

### 核心 DLL（4个 + 4个 pdb）

| 文件 | 说明 |
|------|------|
| SSMSBoostPlus.SSMS.dll | SSMS 扩展主程序 |
| Core.dll | 核心功能层 |
| AI.dll | AI 服务封装 |
| Shared.dll | 共享库 |
| *.pdb | 调试符号（需同步部署） |

### 第三方依赖（14个）

```
DiffPlex.dll
Serilog.dll
Serilog.Sinks.File.dll
Serilog.Enrichers.Environment.dll
Microsoft.Data.SqlClient.dll
Newtonsoft.Json.dll
Microsoft.Bcl.AsyncInterfaces.dll
Microsoft.Extensions.Configuration.dll
Microsoft.Extensions.Configuration.Json.dll
Microsoft.Extensions.Configuration.Binder.dll
Microsoft.SqlServer.TransactSql.ScriptDom.dll
System.Net.Http.dll
```

### PkgDef 文件

| 文件 | 说明 |
|------|------|
| SSMSBoostPlus.SSMS.pkgdef | SSMS 包注册信息 |

## 部署流程（deploy-to-ssms.bat 内部）

1. **Step 1**：检查 SSMS 是否运行，若运行则退出
2. **Step 2**：清空目标目录
3. **Step 3**：复制 PkgDef 文件
4. **Step 4**：复制核心 DLL + PDB
5. **Step 5**：注册 PkgDef（regsvr32 或 VSReg.exe）
6. **Step 6**：复制第三方依赖
7. **Step 7**：验证部署完整性

## 故障排查

| 症状 | 可能原因 | 解决方案 |
|------|----------|----------|
| DLL 加载失败 | SSMS 未关闭 | 关闭所有 SSMS 窗口后重试 |
| PkgDef 未生效 | 注册失败 | 以管理员权限重新运行脚本 |
| 版本不一致 | 旧 DLL 残留 | 删除目标目录后重新部署 |