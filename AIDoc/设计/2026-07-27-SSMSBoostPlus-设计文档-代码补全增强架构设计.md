---
title: SQL代码智能补全增强 - 架构设计文档
date: 2026-07-27
author: Architect Agent
tags: [架构设计, 详细设计, 代码补全, 表结构缓存, 字段补全, schema补全]
---

# SQL代码智能补全增强 - 架构设计文档

## 1. 文档信息

| 项目 | 内容 |
|------|------|
| 版本号 | v1.0 |
| 创建日期 | 2026-07-27 |
| 作者 | Architect Agent |
| 状态 | 初稿 |
| 基于需求规格 | `AIDoc/需求/2026-07-26-SSMSBoostPlus-需求文档-代码补全增强需求规格.md` v1.0 |
| 门控状态 | G1 ✅ G2 ✅ |

---

## 2. 整体架构

### 2.1 架构总览

```
┌──────────────────────────────────────────────────────────────┐
│                    SqlTemplateCompletionService                │
│  (命令过滤器 + 弹窗管理 + 补全决策引擎)                         │
│                                                                │
│  ┌─────────────┐  ┌──────────────┐  ┌───────────────────┐    │
│  │OnCharTyped  │  │OnSpaceTyped  │  │ExpandSelected     │    │
│  │(决策树入口)  │  │(空格触发)    │  │Template(展开逻辑)  │    │
│  └──────┬──────┘  └──────┬───────┘  └────────┬──────────┘    │
│         │                │                    │               │
│         ▼                ▼                    ▼               │
│  ┌────────────────────────────────────────────────────┐      │
│  │  跨库检测 DetectCrossDbContextV2                   │      │
│  │  上下文关键词 GetSqlContextBeforeWord              │      │
│  │  字段/别名检测 HandleFieldOrAliasCompletion       │      │
│  └───────────────────────┬────────────────────────────┘      │
│                          │                                    │
│                          ▼                                    │
│  ┌────────────────────────────────────────────────────┐      │
│  │  CompletionCandidate 生成 + 排序 + 弹窗渲染         │      │
│  │  ShowOrUpdatePopup / HidePopup / CreatePopup       │      │
│  └────────────────────────────────────────────────────┘      │
└─────────────────────────────────┬──────────────────────────────┘
                                  │ 调用
                                  ▼
┌──────────────────────────────────────────────────────────────┐
│                   DatabaseObjectCacheService                  │
│  (数据库对象、Schema、列信息、SP参数的缓存管理)                 │
│                                                                │
│  ┌──────────┐ ┌──────────┐ ┌──────────┐ ┌───────────────┐   │
│  │对象缓存   │ │Schema    │ │列信息    │ │SP参数        │   │
│  │(现有)     │ │缓存(新增) │ │缓存(新增) │ │缓存(新增)    │   │
│  └──────────┘ └──────────┘ └──────────┘ └───────────────┘   │
│                                                                │
│  ┌────────────────────────────────────────────────────┐      │
│  │  本地文件持久化 (按DB分文件)                         │      │
│  │  GetCacheFilePath / LoadCacheFromFile /             │      │
│  │  SaveCacheToFile / DeleteDbCache                    │      │
│  └────────────────────────────────────────────────────┘      │
└──────────────────────────────────────────────────────────────┘

┌──────────────────────────────────────────────────────────────┐
│              SSMSBoostPlusPackage（新增菜单命令）               │
│  ┌────────────────────────────────────────────────────┐      │
│  │  打开脚本菜单 (OpenScriptAndAutoCommentAsync)       │      │
│  └────────────────────────────────────────────────────┘      │
└──────────────────────────────────────────────────────────────┘
```

### 2.2 模块依赖关系

```
DatabaseObjectCacheService（新增缓存维度）
    │
    ├── GetSchemaCandidatesForDb(dbName, prefix)      → 新增
    ├── GetTableColumns(tableName, prefix)             → 新增
    ├── GetMatchingObjectsForDbAndSchema(...)          → 新增
    ├── ResolveTableName(wordBeforeDot)                → 新增
    ├── GetCacheFilePath(server, db)                   → 新增（分文件）
    ├── LoadCacheFromFile(server, db)                  → 新增（分文件）
    └── SaveCacheToFile(cache)                         → 新增（分文件）

SqlTemplateCompletionService（核心决策引擎修改）
    │
    ├── DetectCrossDbContextV2(textView)               → 重写（增强版）
    ├── HandleCrossDbCompletion(dbName, schema, prefix)→ 重写
    ├── HandleFieldOrAliasCompletion(textView, word)   → 新增
    ├── ResolveAliasMap(textView)                      → 新增
    ├── OnCharTyped()                                  → 修改（决策树重构）
    └── ExpandSelectedTemplate()                       → 修改（SP参数展开）

SSMSBoostPlusPackage（新增菜单）
    └── OpenScriptAndAutoCommentAsync()                → 新增
```

---

## 3. 详细类设计

### 3.1 DatabaseObjectCacheService 增强

#### 新增字段

```csharp
public class DatabaseObjectCacheService
{
    // === 现有字段 ===
    private readonly ConcurrentDictionary<string, DatabaseObjectCache> _cache = new();
    private string? _currentCacheKey;
    private string? _currentServer;

    // === 新增字段 ===
    private readonly string _cacheDirectory;                    // 缓存目录路径
    private readonly TimeSpan _cacheExpiration = TimeSpan.FromHours(24); // 文件缓存有效期
}
```

#### 新增方法

| 方法 | 签名 | 说明 |
|------|------|------|
| `GetSchemaCandidatesForDb` | `List<CompletionCandidate> GetSchemaCandidatesForDb(string dbName, string prefix)` | 获取指定DB的Schema列表，转为补全候选 |
| `GetTableColumns` | `List<ColumnInfo>? GetTableColumns(string tableName, string prefix)` | 获取表的列信息，支持前缀过滤 |
| `GetMatchingObjectsForDbAndSchema` | `List<CompletionCandidate> GetMatchingObjectsForDbAndSchema(string dbName, string schema, string prefix, CompletionCategory category)` | 在指定DB+Schema中搜索对象 |
| `ResolveTableName` | `string? ResolveTableName(string name)` | 检查名称是否为已知表名，返回完整表名 |
| `EnsureDatabaseLoadedAsync` | `Task EnsureDatabaseLoadedAsync(string dbName)` | 确保指定DB的缓存已加载（从文件或数据库） |
| `GetCacheFilePath` | `string GetCacheFilePath(string server, string database)` | 构建分文件的完整路径 |
| `LoadCacheFromFile` | `DatabaseObjectCache? LoadCacheFromFile(string server, string database)` | 从分文件读取缓存 |
| `SaveCacheToFile` | `void SaveCacheToFile(DatabaseObjectCache cache)` | 将缓存保存到分文件 |
| `DeleteDbCache` | `void DeleteDbCache(string server, string database)` | 删除指定DB的缓存文件 |

#### 现有方法修改

| 方法 | 修改内容 |
|------|---------|
| `TryAutoLoadAsync` | 新增：尝试从本地文件加载当前DB缓存；同步加载Schema列表；保存到分文件 |
| `FetchAndCacheAsync` | 新增：拉取Schema列表（`sys.schemas`）、列信息（`sys.columns`）、SP参数（`sys.parameters`） |
| `RefreshCacheForCurrentConnectionAsync` | 新增：刷新后重新保存到分文件 |

---

### 3.2 SqlTemplateCompletionService 增强

#### 新增字段

```csharp
// 无新增字段（所有状态复用现有字段）
```

#### 新增方法

| 方法 | 签名 | 说明 |
|------|------|------|
| `DetectCrossDbContextV2` | `(string? dbName, string? schemaName, string objectPrefix)` | 增强版跨库检测，支持 `DBName.Schema.Obj` 三级模式 |
| `HandleCrossDbCompletion` | `void HandleCrossDbCompletion(string dbName, string? schemaName, string objectPrefix)` | 跨库补全分支处理（Schema/对象） |
| `HandleFieldOrAliasCompletion` | `void HandleFieldOrAliasCompletion(IWpfTextView textView, string wordBeforeDot)` | 字段/别名补全分支处理 |
| `ResolveAliasMap` | `Dictionary<string, string> ResolveAliasMap(IWpfTextView textView)` | 从当前语句解析别名→表名映射 |
| `GetContextBeforeDbName` | `string GetContextBeforeDbName(IWpfTextView textView, int caretPos)` | 从 DB 名称前读取上下文关键词（已存在，优化） |

#### 现有方法修改

| 方法 | 修改内容 |
|------|---------|
| `OnCharTyped` | **重构决策树**：1) 先检测跨库 → 2) 检测字段/别名 → 3) 原逻辑 |
| `ExpandSelectedTemplate` | 新增：SP/函数选中时检查参数列表，直接展开为含参数的完整语句 |
| `OnSpaceTyped` | 优化：空格触发时，若在 `DBName.` 后则进入跨库分支 |

---

## 4. 数据流图

### 4.1 OnCharTyped 新决策树（含跨库 + 字段检测）

```
[用户输入字符]
    │
    ▼
OnCharTyped()
    │
    ├── 1. (新增) DetectCrossDbContextV2(textView)
    │   ├── 检测到: DBName.Schema.ObjectPrefix
    │   │   └── HandleCrossDbCompletion(dbName, schemaName, prefix)
    │   │       ├── 有 Schema → 搜索该 Schema 下匹配对象 → ShowOrUpdatePopup + return
    │   │       └── 无 Schema → 判上下文关键词
    │   │           ├── EXEC → 跳过Schema → 搜索该DB的SP/函数 → ShowOrUpdatePopup + return
    │   │           └── FROM/JOIN → 显示Schema列表 → ShowOrUpdatePopup + return
    │   │
    │   └── 未检测到跨库 → ↓
    │
    ├── 2. GetWordBeforeCaret → word
    │   ├── word 为空
    │   │   ├── 空格触发中 → 不关闭弹窗
    │   │   ├── 刚隐藏(<200ms) → 不重开
    │   │   └── 其他 → HidePopup + return
    │   │
    │   └── word 不为空 → ↓
    │
    ├── 3. (新增) 检测 "X." 模式 → 字段/别名补全
    │   ├── 是 "TableName." 或 "Alias." → HandleFieldOrAliasCompletion() + return
    │   └── 否 → ↓
    │
    ├── 4. 获取上下文关键词 + 确定分类
    │
    ├── 5. 构建候选项
    │   ├── 关键词 (前缀索引)
    │   ├── 模板 (缓存)
    │   ├── 数据库对象 (按分类过滤)
    │   └── DB 名称 (去重后)
    │
    ├── 6. 排序 + ShowOrUpdatePopup
    │
    └── 7. 完成
```

### 4.2 ExpandSelectedTemplate 新逻辑（含SP参数展开）

```
[用户选中候选项]
    │
    ▼
ExpandSelectedTemplate()
    │
    ├── 获取选中项 candidate
    │
    ├── candidate.ItemType == DatabaseObject
    │   ├── TypeCode == "P"/"FN"/"IF"/"TF" (SP/函数)
    │   │   ├── 从缓存读取 Parameters
    │   │   │   ├── 有参数 → 构建含参数的 InsertText
    │   │   │   │   ├── SP: "SPName @p1, @p2, ..."
    │   │   │   │   └── 函数: "FuncName(@p1, @p2, ...)"
    │   │   │   └── 无参数 → 原有逻辑 "SPName "
    │   │   └── DoExpand → 光标定位在首参后
    │   │
    │   └── 其他对象类型 (表/视图) → 原有逻辑 "[schema].[name] "
    │
    ├── candidate.ItemType == Database
    │   └── 原有逻辑 "[DBName]." → 自动弹出该DB的Schema/对象
    │
    ├── candidate.ItemType == Schema (新增)
    │   └── 插入 "Schema." → 触发 OnCharTyped 进入跨库检测
    │
    ├── candidate.ItemType == Keyword
    │   └── 原有逻辑 "keyword "
    │
    └── candidate.ItemType == Template
        └── 原有逻辑 展开模板内容
```

### 4.3 缓存加载数据流

```
缓存读取路径：

用户输入 "MESDATA."
    │
    ▼
DetectCrossDbContextV2 → dbName="MESDATA"
    │
    ▼
HandleCrossDbCompletion("MESDATA", null, "")
    │
    ├── _cacheService.EnsureDatabaseLoadedAsync("MESDATA")
    │   │
    │   ├── 检查内存 _cache[$"{server}_MESDATA"] 是否存在
    │   │   ├── 存在 → 直接使用
    │   │   └── 不存在 → ↓
    │   │
    │   ├── 检查本地文件 cache/{server}_MESDATA_object_cache.json 是否存在
    │   │   ├── 存在且未过期 → 反序列化 → 加载到内存 → 重建前缀索引
    │   │   └── 不存在/过期 → ↓
    │   │
    │   └── 从数据库拉取
    │       ├── 查询 sys.objects → 对象列表
    │       ├── 查询 sys.schemas → Schema列表
    │       ├── 查询 sys.columns → 列信息（当前DB）
    │       ├── 查询 sys.parameters → SP参数（当前DB）
    │       ├── 构建前缀索引
    │       ├── 保存到分文件
    │       └── 加载到内存
    │
    └── 查询 Schema 列表 → 生成 Schema 候选 → 显示弹窗


缓存保存路径：

FetchAndCacheAsync 完成
    │
    ▼
生成 DatabaseObjectCache 对象
    │
    ▼
SaveCacheToFile(cache)
    │
    ├── 构建文件路径: GetCacheFilePath(server, database)
    │
    ├── JsonConvert.SerializeObject(cache, Formatting.Indented)
    │
    ├── 确保 cache/ 目录存在
    │
    └── File.WriteAllText(filePath, json, Encoding.UTF8)
```

---

## 5. 修改点清单（按文件）

### 5.1 `src/SSMS/Services/DatabaseObjectCacheService.cs`

**新增字段/属性**：
```csharp
private readonly string _cacheDirectory;
private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(24);
```

**新增方法**：
```csharp
// == Schema 补全 ==
public List<CompletionCandidate> GetSchemaCandidatesForDb(string dbName, string prefix)

// == 字段补全 ==
public List<ColumnInfo>? GetTableColumns(string tableName, string prefix)
public string? ResolveTableName(string name)

// == 跨库+Schema过滤 ==
public List<CompletionCandidate> GetMatchingObjectsForDbAndSchema(
    string dbName, string schema, string prefix, CompletionCategory category)

// == 按DB分文件持久化 ==
private string GetCacheFilePath(string server, string database)
private DatabaseObjectCache? LoadCacheFromFile(string server, string database)
private void SaveCacheToFile(DatabaseObjectCache cache)
public void DeleteDbCache(string server, string database)
public bool IsCacheExpired(string server, string database)

// == 延迟加载 ==
public async Task EnsureDatabaseLoadedAsync(string dbName)
```

**修改方法**：
```csharp
// TryAutoLoadAsync
// 修改点：增加从本地分文件加载、同步拉取Schema列表、保存到分文件

// FetchAndCacheAsync
// 修改点：增加 sys.schemas / sys.columns / sys.parameters 查询

// RefreshCacheForCurrentConnectionAsync
// 修改点：刷新后重新保存分文件
```

**新增 SQL 查询**（在 FetchAndCacheAsync 或独立方法中）：

```csharp
// 1. Schema 列表
private const string SchemaQuery = @"
    SELECT name FROM [{0}].sys.schemas
    WHERE name NOT LIKE 'db_%'
    ORDER BY name";

// 2. 列信息（批量查询，减少数据库往返）
private const string ColumnsQuery = @"
    SELECT 
        OBJECT_SCHEMA_NAME(c.object_id) AS SchemaName,
        OBJECT_NAME(c.object_id) AS TableName,
        c.name AS ColumnName,
        TYPE_NAME(c.user_type_id) AS DataType,
        c.max_length, c.precision, c.scale,
        c.is_nullable, c.is_identity,
        OBJECT_DEFINITION(c.default_object_id) AS DefaultValue,
        ep.value AS Description
    FROM [{0}].sys.columns c
    LEFT JOIN [{0}].sys.extended_properties ep
        ON ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description'
    WHERE c.object_id IN (
        SELECT object_id FROM [{0}].sys.objects
        WHERE type IN ('U','V')
    )
    ORDER BY c.object_id, c.column_id";

// 3. SP 参数（批量查询）
private const string ParametersQuery = @"
    SELECT 
        OBJECT_SCHEMA_NAME(p.object_id) AS SchemaName,
        OBJECT_NAME(p.object_id) AS ProcName,
        p.name AS ParameterName,
        TYPE_NAME(p.user_type_id) AS DataType,
        p.max_length, p.precision, p.scale,
        p.is_output, p.has_default_value, p.default_value
    FROM [{0}].sys.parameters p
    WHERE p.object_id IN (
        SELECT object_id FROM [{0}].sys.objects
        WHERE type IN ('P','FN','IF','TF')
    )
    ORDER BY p.object_id, p.parameter_id";
```

### 5.2 `src/SSMS/Services/SqlTemplateCompletionService.cs`

**新增方法**：
```csharp
// == 增强版跨库检测 ==
private (string? dbName, string? schemaName, string objectPrefix) DetectCrossDbContextV2(
    IWpfTextView textView)

// == 跨库补全分支 ==
private void HandleCrossDbCompletion(
    string dbName, string? schemaName, string objectPrefix)

// == 字段/别名补全 ==
private void HandleFieldOrAliasCompletion(
    IWpfTextView textView, string wordBeforeDot)
private Dictionary<string, string> ResolveAliasMap(IWpfTextView textView)

// == 辅助方法 ==
private string ExtractDbNameFromBracket(
    string lineText, int bracketPos, int dotPos, string currentWord)
```

**修改方法**：

**`OnCharTyped` 重构**：
```csharp
internal void OnCharTyped()
{
    // 记录文本变化时间
    _lastTextChangeTime = DateTime.UtcNow;

    try
    {
        if (_activeTextView == null) return;

        // ★★★ 步骤1：优先检测跨库上下文 ★★★
        var (crossDbName, schemaName, objPrefix) = DetectCrossDbContextV2(_activeTextView);
        if (crossDbName != null)
        {
            HandleCrossDbCompletion(crossDbName, schemaName, objPrefix);
            return;
        }

        // 步骤2：获取光标前的单词
        var (word, wordStart, _) = GetWordBeforeCaret(_activeTextView);

        // 步骤3：空单词处理
        if (string.IsNullOrEmpty(word))
        {
            if (_isPopupVisible && _hasSpaceTrigger) return;
            if (!_isPopupVisible && (DateTime.UtcNow - _lastUserHideTime).TotalMilliseconds < 200)
                return;
            HidePopup();
            return;
        }

        // ★★★ 步骤4：检测字段/别名补全 ★★★
        // 检查光标前是否是 "word."
        if (word.Length > 0 && _activeTextView.Caret.Position.BufferPosition.Position > wordStart + word.Length)
        {
            var charBeforeWord = _activeTextView.TextBuffer.CurrentSnapshot
                .GetText(wordStart - 1, 1);
            if (charBeforeWord == ".")  // 用户输入 "TableName." 或 "Alias."
            {
                // word 是 . 之前的内容，但 GetWordBeforeCaret 不包含 .
                // 需要重新检测：从光标位置往回找
                var (isFieldCompletion, completionResult) = TryDetectFieldCompletion(_activeTextView);
                if (isFieldCompletion)
                {
                    // 填入结果后 return
                    return;
                }
            }
        }

        // 实际实现：调用 HandleFieldOrAliasCompletion
        // 原理：检测光标前 X 字符是否为 '.'，X 为 word.Length+1
        if (wordStart >= 1)
        {
            var snapshot = _activeTextView.TextBuffer.CurrentSnapshot;
            var charBeforeStart = snapshot.GetText(wordStart - 1, 1);
            if (charBeforeStart == ".")
            {
                var wordBeforeDot = GetWordBeforeDot(_activeTextView, wordStart - 1);
                if (!string.IsNullOrEmpty(wordBeforeDot))
                {
                    HandleFieldOrAliasCompletion(_activeTextView, wordBeforeDot);
                    // 注意：如果 HandleFieldOrAliasCompletion 显示了弹窗，return
                    // 如果没有匹配（无字段），则继续走原有逻辑
                    if (_isPopupVisible) return;
                }
            }
        }

        // 步骤5-7：原有逻辑（上下文关键词、分类、候选、排序、弹窗）
        var contextKeyword = GetSqlContextBeforeWord(_activeTextView, wordStart);
        // ... 后续原有逻辑 ...
    }
    catch (Exception ex) { /* ... */ }
}
```

**`ExpandSelectedTemplate` 修改**（SP参数展开部分）：
```csharp
// 在 ExpandSelectedTemplate 中，处理 DatabaseObject 类型时新增分支
if (candidate.ItemType == CompletionItemType.DatabaseObject)
{
    insertText = candidate.InsertText ?? candidate.DisplayText;

    // ★★★ 新增：SP/函数参数展开 ★★★
    if (candidate.Source is DatabaseObjectInfo objInfo &&
        (objInfo.TypeCode == "P" || objInfo.TypeCode == "FN" ||
         objInfo.TypeCode == "IF" || objInfo.TypeCode == "TF"))
    {
        if (objInfo.Parameters != null && objInfo.Parameters.Count > 0)
        {
            var nonOutputParams = objInfo.Parameters
                .Where(p => !p.IsOutput)
                .Select(p => p.Name);

            if (objInfo.TypeCode == "FN" || objInfo.TypeCode == "TF")
            {
                // 函数：圆括号包裹
                insertText = $"{objInfo.Name}({string.Join(", ", nonOutputParams)})";
            }
            else
            {
                // SP：空格分隔
                insertText = $"{objInfo.Name} {string.Join(", ", nonOutputParams)} ";
            }
        }
        else
        {
            insertText = objInfo.Name + " ";
        }
    }
    else
    {
        insertText = (candidate.InsertText ?? candidate.DisplayText) + " ";
    }
}
```

### 5.3 `src/SSMS/SSMSBoostPlusPackage.cs`

**新增菜单命令**：
```csharp
// 在 Package 初始化中注册菜单命令
await _openScriptCommand.InitializeAsync(this);

// 新增命令处理
private async Task OpenScriptAndAutoCommentAsync(object sender, EventArgs e)
{
    // ... 文件选择 + 注释逻辑 ...
}
```

**新增 VSCT 定义**：
```xml
<Button guid="guidSSMSBoostPlusCmdSet" id="cmdidOpenScript" priority="0x0100">
    <MenuText>打开脚本</MenuText>
    <ButtonText>打开脚本(&S)</ButtonText>
    <ToolTipText>打开 .sql 脚本文件并自动注释 ALTER/CREATE 语句</ToolTipText>
</Button>
```

### 5.4 `src/SSMS/Services/DatabaseObjectCacheService.cs` — CompletionCandidate/CompletionItemType

**新增枚举值**（在 `DatabaseObjectCacheService.cs` 文件末尾）：

```csharp
public enum CompletionItemType
{
    Template,
    Keyword,
    DatabaseObject,
    Database,

    // v1.1 新增
    Schema,
    Field,
}
```

---

## 6. 分文件缓存实现细节

### 6.1 GetCacheFilePath

```csharp
private string GetCacheFilePath(string server, string database)
{
    var baseDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
    var cacheDir = Path.Combine(baseDir, "cache");

    if (!Directory.Exists(cacheDir))
        Directory.CreateDirectory(cacheDir);

    var safeServer = server
        .Replace('.', '_')
        .Replace(':', '_')
        .Replace('\\', '_')
        .Replace('/', '_');

    return Path.Combine(cacheDir, $"{safeServer}_{database}_object_cache.json");
}
```

### 6.2 LoadCacheFromFile

```csharp
private DatabaseObjectCache? LoadCacheFromFile(string server, string database)
{
    var filePath = GetCacheFilePath(server, database);
    if (!File.Exists(filePath))
        return null;

    try
    {
        var fileInfo = new FileInfo(filePath);
        if (DateTime.UtcNow - fileInfo.LastWriteTimeUtc > CacheExpiration)
        {
            _logger.Information($"[Cache] 缓存已过期: {filePath}");
            return null;  // 过期，触发重建
        }

        var json = File.ReadAllText(filePath, Encoding.UTF8);
        var cache = JsonConvert.DeserializeObject<DatabaseObjectCache>(json);
        if (cache != null)
        {
            // 重建前缀索引
            cache.RebuildPrefixIndex();
        }
        return cache;
    }
    catch (Exception ex)
    {
        _logger.Warning(ex, $"[Cache] 读取缓存文件失败: {filePath}");
        return null;  // 读取失败，触发重建
    }
}
```

### 6.3 启动时加载优化

```csharp
public async Task TryAutoLoadAsync()
{
    // ... 获取连接信息 ...

    // ★★★ 尝试从本地分文件加载当前DB缓存 ★★★
    var cached = LoadCacheFromFile(server, currentDbName);
    if (cached != null)
    {
        _cache[cacheKey] = cached;
        _currentCacheKey = cacheKey;
        _currentServer = server;
        _logger.Information($"[Cache] ✅ 从本地文件加载缓存: {currentDbName} ({cached.Objects.Count} 对象)");

        // 后台异步刷新（确保数据最新，不阻塞UI）
        _ = Task.Run(async () =>
        {
            try { await RefreshCacheForCurrentConnectionAsync(); }
            catch (Exception ex) { _logger.Error(ex, "[Cache] 后台刷新失败"); }
        });

        return;
    }

    // 本地无缓存或已过期 → 从数据库拉取
    // ... 原有逻辑 ...
}
```

### 6.4 缓存重建机制（核心流程）

#### 触发场景

| 场景 | 说明 |
|------|------|
| **启动时本地缓存过期** | 最常见的触发时机，SSMS 启动后打开 SQL 编辑器 |
| **手动触发刷新** | 用户主动执行"刷新缓存"命令 |
| **跨库懒加载过期** | 用户输入 `DBName.` 时检测到目标 DB 无缓存或已过期 |

#### 重建流程（启动时本地缓存过期）

```
SSMS 启动 → 用户打开 SQL 编辑器
    │
    ▼
SqlTemplateCompletionService.ProcessActiveDocumentAsync()
    │
    ├── 触发 DatabaseObjectCacheService.TryAutoLoadAsync()
    │   │
    │   ├── 1. 读取本地分文件 cache/{server}_{db}_object_cache.json
    │   │   ├── 文件不存在 → 直接走 3（数据库拉取）
    │   │   └── 文件存在 → 检查 FileInfo.LastWriteTimeUtc
    │   │       ├── 距今 < 24小时 → ✅ 使用本地缓存，加载到内存，结束
    │   │       └── 距今 >= 24小时 → ⚠️ 已过期，走 2
    │   │
    │   ├── 2. 本地缓存过期，但**不阻塞用户**
    │   │   ├── 先将旧缓存加载到内存（用户可立即使用补全功能）
    │   │   ├── 同时在后台启动异步任务重建缓存
    │   │   │   ├── 后台查询 sys.objects / sys.schemas / sys.columns / sys.parameters
    │   │   │   ├── 完成后保存到分文件
    │   │   │   ├── 更新内存中的缓存
    │   │   │   └── 记录日志 "MESDATA 缓存已后台重建完成"
    │   │   │
    │   │   └── 返回（用户已可使用旧数据，UI 不卡顿）
    │   │
    │   └── 3. 无本地缓存（首次启动或手动删除缓存文件）
    │       ├── 从数据库拉取（前台等待，SSMS 显示等待动画）
    │       ├── 完成后保存到分文件 + 加载到内存
    │       └── 后续启动都走快速路径（步骤 1）
    │
    └── 返回
```

#### 关键设计要点

**过期不删文件**：
- 过期的分文件**不删除**，作为兜底
- 后台重建时直接覆盖写入新内容
- 即使后台重建失败，内存中仍有旧数据，不影响当前使用

**重建与使用的并发**：
```
后台重建中 ──> 用户正在使用旧缓存 ──> 后台重建完成 ──> 更新内存缓存
                                                   │
                                                   └─> 下次 OnCharTyped 时使用新数据
```
用户永远不会被阻塞等待重建完成。

**多窗口/多连接竞态**：
- 使用 `SemaphoreSlim(1)` 保证同一 DB 同一时间只有一个重建任务
- 第二个请求发现重建已在进行中 → 直接返回，等待结果即可

```csharp
private readonly SemaphoreSlim _refreshSemaphore = new(1, 1);

private async Task RefreshCacheInBackgroundAsync(string dbName)
{
    if (!await _refreshSemaphore.WaitAsync(0))  // 不等待，立即返回
    {
        _logger.Debug($"[Cache] {dbName} 后台重建已在进行中，跳过");
        return;
    }

    try
    {
        // 执行后台重建
        await FetchAndCacheAsync(connectionString, dbName);
        _logger.Information($"[Cache] ✅ {dbName} 后台重建完成");
    }
    catch (Exception ex)
    {
        _logger.Error(ex, $"[Cache] {dbName} 后台重建失败");
        // 失败不影响：内存中仍有旧数据
    }
    finally
    {
        _refreshSemaphore.Release();
    }
}
```

**重建失败处理**：
| 失败场景 | 处理 |
|---------|------|
| 数据库连接断开 | 记录 Error 日志，不抛异常，内存中旧数据继续使用 |
| SQL 查询超时 | 记录 Error 日志，不抛异常 |
| 权限不足 | 记录 Error 日志，不抛异常 |
| 文件写入失败 | 记录 Warning 日志，内存缓存已更新但下次启动时重拉 |

---

## 7. 实现顺序与依赖关系

### Phase 1：Bug 修复（独立模块，无外部依赖）

| 步骤 | 内容 | 涉及方法 | 依赖 |
|------|------|---------|------|
| 1.1 | 选区删除 | `InsertCharAndRefresh`, `ExpandSelectedTemplate` | 无 |
| 1.2 | ESC 关闭 | `CompletionCommandFilter.Exec` | 无 |
| 1.3 | 跨库上下文提前检测 | `OnCharTyped` 顺序调整 | 无 |
| 1.4 | DB名称去重 | `AddDbNameCandidates` | 无 |

### Phase 2：Schema + 分文件缓存（依赖 Phase 1.3）

| 步骤 | 内容 | 涉及方法 | 依赖 |
|------|------|---------|------|
| 2.1 | 新增 Schema/Field 枚举 | `CompletionItemType` | 无 |
| 2.2 | 实现分文件持久化 | `GetCacheFilePath`, `LoadCacheFromFile`, `SaveCacheToFile` | 无 |
| 2.3 | 实现 Schema 缓存查询 | `FetchAndCacheAsync` 增加 `sys.schemas` 查询 | 2.2 |
| 2.4 | 实现 Schema 候选生成 | `GetSchemaCandidatesForDb` | 2.3 |
| 2.5 | 实现跨库智能分流 | `HandleCrossDbCompletion`（Schema/EXEC分支） | 1.3 |
| 2.6 | 修改启动加载逻辑 | `TryAutoLoadAsync` 增加分文件读取 | 2.2 |

### Phase 3：表结构缓存 + 字段补全（依赖 Phase 2）

| 步骤 | 内容 | 涉及方法 | 依赖 |
|------|------|---------|------|
| 3.1 | 实现列信息缓存查询 | `FetchAndCacheAsync` 增加 `sys.columns` 查询 | 2.2 |
| 3.2 | 实现 `GetTableColumns` | 从缓存获取列信息 | 3.1 |
| 3.3 | 实现 `ResolveTableName` | 从对象缓存查找表名 | 2.3 |
| 3.4 | 实现 `ResolveAliasMap` | 正则解析 FROM/JOIN 别名 | 无 |
| 3.5 | 实现 `HandleFieldOrAliasCompletion` | 字段补全分支 | 3.2, 3.3, 3.4 |
| 3.6 | 修改 `OnCharTyped` 增加字段检测 | 决策树 Step 3 | 3.5 |
| 3.7 | 实现表结构弹窗 UI | 新增浮层 Popup | 3.1 |

### Phase 4：SP参数补全（依赖 Phase 2）

| 步骤 | 内容 | 涉及方法 | 依赖 |
|------|------|---------|------|
| 4.1 | 实现 SP 参数缓存查询 | `FetchAndCacheAsync` 增加 `sys.parameters` 查询 | 2.2 |
| 4.2 | 修改 `ExpandSelectedTemplate` | 选中SP时展开参数列表 | 4.1 |

### Phase 5：自动注释（独立模块）

| 步骤 | 内容 | 涉及方法 | 依赖 |
|------|------|---------|------|
| 5.1 | 新增菜单 VSCT 定义 | SSMSBoostPlusPackage.vsct | 无 |
| 5.2 | 实现文件打开+注释逻辑 | `OpenScriptAndAutoCommentAsync` | 无 |

---

## 8. 风险与应对

| 风险 | 影响 | 可能性 | 应对方案 |
|------|------|--------|---------|
| `sys.columns` 批量查询数据量大（1000+表） | 首次加载慢 | 中 | 使用并行查询 + `SqlDataReader` 流式读取；仅缓存列名+类型，描述信息按需加载 |
| 分文件缓存目录创建权限不足 | 无法持久化 | 低 | 降级为仅内存缓存，不影响功能使用 |
| `ResolveAliasMap` 正则误匹配复杂 SQL（CTE、子查询） | 字段补全异常 | 中 | 严格限制搜索范围为当前行的 FROM/JOIN 关键字；匹配失败时静默降级 |
| SP参数过多（>10个）导致展开行过长 | 代码可读性下降 | 低 | 仅展开必填参数（`HasDefault=false`），可选参数省略 |
| 多服务器切换时缓存文件混乱 | 加载错误DB的缓存 | 低 | 文件名包含 `safeServer`，不同服务器名不同文件，不会混淆 |
