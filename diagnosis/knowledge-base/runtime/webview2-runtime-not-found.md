# WebView2RuntimeNotFoundException

## 匹配特征

- 异常类型: `Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException`
- 关键方法: `WebView2RuntimeHelper.TryFindWebView2RuntimePath`
- 消息关键词: `Couldn't find a compatible Webview2 Runtime installation`

## 常见根因

1. **SetupMetrics 目录误选**：`WebView2RuntimeHelper` 使用字母降序排序误将 `SetupMetrics` 目录当作版本目录。`SetupMetrics` 是 Edge 安装的指标/元数据目录，不含运行时二进制文件。修复方法：增加版本号正则过滤（`^\d+\.\d+\.\d+\.\d+$`）和 `msedgewebview2.exe` 二进制存在性校验。
2. **WebView2 Runtime 未安装**：EdgeWebView 安装目录完全不存在或未安装 Evergreen WebView2 Runtime。此时 SDK 会回退到默认查找逻辑，但因注册表缺失也需要兜底。
3. **注册表 EdgeUpdate 缺失**：当 EdgeUpdate 注册表键值缺失时，SDK 默认查找失败，才会调用 `WebView2RuntimeHelper` 做文件系统兜底。

## 修复方案

### 方案A：版本目录过滤 + 二进制校验（推荐，已验证）

修改 `src/SSMS/Services/WebView2RuntimeHelper.cs`：

```csharp
private static readonly Regex VersionDirPattern = new(
    @"^\d+\.\d+\.\d+\.\d+$",
    RegexOptions.Compiled);

// 在 TryFindWebView2RuntimePath 方法中替换原本的排序逻辑：
var allDirs = Directory.GetDirectories(basePath)
    .Select(Path.GetFileName)
    .ToArray();

var versionDirs = allDirs
    .Where(name => VersionDirPattern.IsMatch(name))
    .Select(name => new
    {
        Name = name,
        FullPath = Path.Combine(basePath, name),
        Version = Version.TryParse(name, out var v) ? v : new Version(0, 0, 0, 0)
    })
    .OrderByDescending(x => x.Version)
    .ToArray();

foreach (var dir in versionDirs)
{
    var runtimeExe = Path.Combine(dir.FullPath, "msedgewebview2.exe");
    if (File.Exists(runtimeExe))
    {
        return dir.FullPath;
    }
}
```

### 方案B：安装 Evergreen WebView2 Runtime

如果用户系统完全未安装 WebView2 Runtime，从以下地址下载安装：
- https://developer.microsoft.com/en-us/microsoft-edge/webview2/

安装后重启 SSMS 即可自动识别。

## 预防措施

- [ ] 文件系统目录查找应使用语义版本排序（`Version` 类）而非字母排序
- [ ] 目录名匹配应采用白名单模式（正则过滤），而非黑名单模式
- [ ] 对运行时路径选择应增加二进制文件存在性校验（`msedgewebview2.exe`）
- [ ] 选择回退路径时增加详细日志，便于后续排查

## 关联源码

- `src/SSMS/Services/WebView2RuntimeHelper.cs` — 核心查找逻辑
- `src/SSMS/UI/Dialogs/AIStreamResultDialog.xaml.cs:103` — 调用方
- `src/SSMS/UI/Chat/Controls/MarkdownViewer.xaml.cs:189` — 调用方（未传 logger）

## 历史案例

- 2026-07-06: 用户点击"AI性能分析"时 WebView2 初始化失败，日志显示错误选择了 `SetupMetrics` 而非 `150.0.4078.48`。已修复：增加版本目录正则过滤和二进制校验。