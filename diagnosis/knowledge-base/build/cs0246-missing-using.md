# CS0246: 找不到类型或命名空间

## 匹配特征

- 错误码: `CS0246`
- 消息关键词: `could not be found (are you missing a using directive or an assembly reference?)`

## 常见根因

1. **缺少 using 指令**: 代码中使用了某个类型但未 `using` 其命名空间
2. **缺少项目引用**: 新增了对另一个项目类型的引用，但 csproj 中未添加 `<ProjectReference>`
3. **缺少 NuGet 包**: 使用了第三方库的类型但未安装对应 NuGet 包
4. **命名空间拼写错误**: using 语句中的命名空间与实际不符
5. **依赖链断裂**: 项目依赖链为 Shared ← Core ← AI ← SSMS，如果 Core 未引用 Shared，SSMS 中无法使用 Shared 的类型

## 修复方案

### 方案A：添加 using

```csharp
// 文件顶部添加
using System.Data.SqlClient;  // 例如缺少 SqlConnection
```

### 方案B：添加项目引用

```xml
<!-- 在 csproj 中添加 -->
<ItemGroup>
  <ProjectReference Include="..\Core\SSMSBoostPlus.Core.csproj" />
</ItemGroup>
```

### 方案C：安装 NuGet 包

```powershell
dotnet add src/SSMS/SSMSBoostPlus.SSMS.csproj package PackageName
```

### 方案D：检查依赖链

```
确认依赖链: Shared ← Core ← AI ← SSMS
SSMS 可直接使用 Core/AI/Shared 的类型
Core 可直接使用 Shared 的类型
Core 不能使用 AI 或 SSMS 的类型（反向引用禁止）
```

## 预防措施

- [ ] 添加新文件后立即 `dotnet build` 验证
- [ ] 使用 IDE 的"快速操作"（Ctrl+.）自动添加 using
- [ ] 检查依赖链方向：禁止 SSMS → Core 反向引用

## 关联源码

- `src/SSMS/SSMSBoostPlus.SSMS.csproj` — 项目引用列表
- `src/Core/SSMSBoostPlus.Core.csproj` — Shared 引用
- `src/AI/SSMSBoostPlus.AI.csproj` — Core + Shared 引用

## 历史案例

- （待积累）