---
title: SQL代码智能补全增强 - 需求规格文档
date: 2026-07-26
author: BA Agent (基于PM PRD v1.1)
tags: [代码补全, 表结构缓存, 字段补全, schema补全, 自动注释, SP参数]
---

# SQL代码智能补全增强 - 需求规格文档

## 1. 文档信息

| 项目 | 内容 |
|------|------|
| 版本号 | v1.0 |
| 创建日期 | 2026-07-26 |
| 作者 | BA Agent |
| 状态 | 初稿 |
| 基于PRD | `AIDoc/需求分析/2026-07-25-SSMSBoostPlus-需求文档-代码补全增强PRD.md` v1.1 |
| 门控状态 | G1 ✅ 已通过 |

### 变更记录

| 版本 | 日期 | 变更内容 | 变更人 |
|------|------|----------|--------|
| v1.0 | 2026-07-26 | 初始版本 | BA Agent |

---

## 2. 模块划分清单

### 2.1 模块总览

| 模块编号 | 功能名称 | 子模块 | 涉及文件 | 优先级 |
|----------|----------|--------|---------|--------|
| M1 | 补全Bug修复 | M1.1 选区删除 / M1.2 ESC关闭 / M1.3 跨库上下文 / M1.4 DB去重 | `SqlTemplateCompletionService.cs` | P0-P1 |
| M2 | Schema补全 | M2.1 Schema缓存 / M2.2 上下文智能分流 / M2.3 Schema弹窗 | `DatabaseObjectCacheService.cs` + `SqlTemplateCompletionService.cs` | P1 |
| M3 | 表结构缓存与展示 | M3.1 列信息缓存 / M3.2 CREATE TABLE脚本生成 / M3.3 结构弹窗UI | `DatabaseObjectCacheService.cs` + UI组件 | P1-P2 |
| M4 | 字段补全 | M4.1 表名上下文检测 / M4.2 别名解析 / M4.3 字段弹窗 | `SqlTemplateCompletionService.cs` | P1-P2 |
| M5 | SP参数补全 | M5.1 SP参数缓存 / M5.2 参数列表展开 | `DatabaseObjectCacheService.cs` + `SqlTemplateCompletionService.cs` | P1 |
| M6 | 自动注释脚本 | M6.1 文件打开事件 / M6.2 ALTER/CREATE检测 / M6.3 注释插入 | 新增文件 + 菜单命令 | P1 |

---

## 3. 详细数据模型

### 3.1 现有模型变更

#### DatabaseObjectCache（新增字段）

```csharp
public class DatabaseObjectCache
{
    // === 现有字段（不变）===
    public string Server { get; set; } = "";
    public string Database { get; set; } = "";
    public DateTime LastUpdated { get; set; }
    public List<DatabaseObjectInfo> Objects { get; set; } = new();
    [JsonIgnore] public Dictionary<string, List<DatabaseObjectInfo>> PrefixIndex { get; set; } = new();

    // === v1.1 新增字段 ===
    /// <summary>数据库中的所有 Schema 名称列表</summary>
    [JsonProperty("schemas")]
    public List<string> Schemas { get; set; } = new();
}
```

#### DatabaseObjectInfo（新增字段）

```csharp
public class DatabaseObjectInfo
{
    // === 现有字段（不变）===
    public string Schema { get; set; } = "";
    public string Name { get; set; } = "";
    public string TypeCode { get; set; } = "";
    public string TypeName { get; set; } = "";

    // === v1.1 新增字段 ===
    /// <summary>表的列信息列表（仅 TypeCode="U" 或 "V" 时有值）</summary>
    [JsonProperty("columns")]
    public List<ColumnInfo>? Columns { get; set; }

    /// <summary>SP/函数的参数列表（仅 TypeCode="P"/"FN"/"IF"/"TF" 时有值）</summary>
    [JsonProperty("parameters")]
    public List<ParameterInfo>? Parameters { get; set; }
}
```

### 3.2 新增模型

#### ColumnInfo（表列信息）

```csharp
public class ColumnInfo
{
    [JsonProperty("name")]          public string Name { get; set; } = "";
    [JsonProperty("dataType")]      public string DataType { get; set; } = "";
    [JsonProperty("maxLength")]     public short MaxLength { get; set; }
    [JsonProperty("precision")]     public byte Precision { get; set; }
    [JsonProperty("scale")]         public byte Scale { get; set; }
    [JsonProperty("isNullable")]    public bool IsNullable { get; set; }
    [JsonProperty("isIdentity")]    public bool IsIdentity { get; set; }
    [JsonProperty("defaultValue")]  public string? DefaultValue { get; set; }
    [JsonProperty("description")]   public string? Description { get; set; }
}
```

#### ParameterInfo（SP/函数参数信息）

```csharp
public class ParameterInfo
{
    [JsonProperty("name")]       public string Name { get; set; } = "";      // 如 "@Id"
    [JsonProperty("dataType")]   public string DataType { get; set; } = "";  // 如 "INT"
    [JsonProperty("maxLength")]  public short MaxLength { get; set; }
    [JsonProperty("isOutput")]   public bool IsOutput { get; set; }
    [JsonProperty("hasDefault")] public bool HasDefault { get; set; }
}
```

### 3.3 新增枚举

```csharp
public enum CompletionItemType
{
    Template,        // 现有
    Keyword,         // 现有
    DatabaseObject,  // 现有
    Database,        // 现有

    // v1.1 新增
    Schema,          // Schema 名称
    Field,           // 表字段名
}
```

> **说明**：SP参数补全不新增枚举，参数列表通过 `InsertText` 直接拼装在 DatabaseObject 的展开文本中。

### 3.4 CompletionCandidate 新增场景用法

| 场景 | ItemType | DisplayText | TypeTag | InsertText | 备注 |
|------|----------|-------------|---------|------------|------|
| Schema名 | Schema | "dbo" | "模式" | "dbo." | 选中后插入带尾随点号 |
| 字段名 | Field | "Id" | "字段" | "Id " | 选中后插入字段名+空格 |
| SP展开含参数 | DatabaseObject | "dbo.GetUserBy" | "SP" | "dbo.GetUserBy @Id, @Name" | InsertText 直接拼参数 |
| 函数展开含参数 | DatabaseObject | "dbo.CalcTotal" | "函数" | "dbo.CalcTotal(@amount)" | InsertText 带括号 |

---

## 4. 详细功能规格

### 4.1 M1.1 — 补全展开时处理文本选区

#### 触发条件
- 编辑器存在 `Selection`（`textView.Selection.SelectedSpans.Count > 0`）
- 触发路径：
  - 路径A：`InsertCharAndRefresh` — TYPECHAR 字符输入
  - 路径B：`ExpandSelectedTemplate` — Enter/Tab/鼠标双击补全

#### 正常流程（路径A：字符输入）
```
1. CompletionCommandFilter.Exec 拦截 TYPECHAR（字母/数字/下划线）
2. 调用 InsertCharAndRefresh(ch)
3. InsertCharAndRefresh:
   a. 获取 _activeTextView.Selection
   b. 若存在选区(SelectedSpan)且非空:
      - _isInternalEdit = true
      - 使用 edit.Delete(selectionSpan) 删除选区内容
      - 在删除后的位置 edit.Insert(ch)
      - edit.Apply()
      - 光标移动到插入位置后
      - 调用 OnCharTyped() 刷新弹窗
   c. 若无选区: 原有 insert + refresh 逻辑
4. 返回 OLECMDERR_E_CANCELED
```

#### 正常流程（路径B：Enter/Tab补全）
```
1. CompletionCommandFilter 拦截 Enter/TAB
2. 调用 ExpandSelectedTemplate()
3. ExpandSelectedTemplate:
   a. 获取编辑器选区（textView.Selection）
   b. 若存在选区:
      - 记录 startPosition = 选区起始, length = 选区长度
      - 用 edit.Replace(selectionSpan, insertText) 替换选区内容
   c. 若无选区: 原有 GetWordBeforeCaret 逻辑
```

#### 边界条件
| 条件 | 行为 |
|------|------|
| 选区跨越多行 | 删除多行选区，在起始位置插入字符 |
| 选区为空（零宽度） | 视为无选区，走原有逻辑 |
| 选区在行尾 | 正常删除选区，光标保持在删除后的位置 |
| 多光标模式（SSMS） | 只处理第一个光标位置的选区 |

---

### 4.2 M1.2 — ESC 关闭弹窗

#### 问题根因
当前 `CompletionCommandFilter` 在 `VSStd2K` 组拦截 `CMDID_CANCEL`（id=2）。但在 SSMS 22 中，ESC 可能走 `VSStd97` 组或其他未知命令组。

#### 修改方案

```csharp
// === 在 CompletionCommandFilter.Exec 中 VSStd2K 分支（现有）===
if (pguidCmdGroup == VSStd2KGuid)
{
    // ... 其他命令处理 ...

    case CMDID_CANCEL:
        _owner.HidePopup();
        return OLECMDERR_E_CANCELED;
}

// === 新增：VSStd97 组也拦截 ESC ===
// VSStd97 中 ESC 通常对应 id=1 (ECMD_CANCEL) 或特定 ID
// 需在 SSMS 22 中实际测试确定

// === 新增：全局兜底处理 ===
// 在 Exec 方法的末尾，任意命令组：
// if (_owner._isPopupVisible && hr >= 0 && 文本长度未变化)
// {
//     检查是否是 ESC 按键（通过键盘状态检测）
// }
```

#### 兜底方案1（推荐）
在 `Exec` 方法的阶段 3.5（兜底逻辑，行 2098-2107）中增加一个分支：

```csharp
// 阶段 3.5 兜底：弹窗可见但文本长度无变化 → 可能是 ESC 或其他命令
// 此时不重新打开弹窗（保持隐藏状态）
if (!_owner._isPopupVisible && popupWasVisible)
{
    int snapshotLengthAfter = _owner._activeTextView?.TextBuffer?.CurrentSnapshot?.Length ?? -1;
    if (snapshotLengthAfter == snapshotLengthBefore && snapshotLengthBefore >= 0)
    {
        // 文本长度未变化 → 很可能是 ESC 关闭
        // 不执行 OnCharTyped（不重新打开弹窗）
        _owner._lastUserHideTime = DateTime.UtcNow;
        return hr;
    }
}
```

---

### 4.3 M1.3 — 小数点后跨库上下文切换

#### 状态机（核心修改）

```
OnCharTyped() 进入
    │
    ├── [新增] DetectCrossDbContextV2(_activeTextView)
    │       │
    │       ├── 检测到跨库模式 (dbName != null)
    │       │   ├── 判断上下文关键词 (FROM/EXEC/JOIN 等)
    │       │   ├── EXEC → ShowCandidatesForDb(dbName, ProceduresAndFunctions)  ↓跳过Schema
    │       │   └── 非EXEC → ShowSchemaCandidatesForDb(dbName)                   ↓显示Schema
    │       │   └── return
    │       │
    │       └── 未检测到跨库
    │           └── ↓ 继续原有逻辑
    │
    ├── GetWordBeforeCaret() → word
    │   ├── word 为空 && 非空格触发 && 非刚隐藏 → HidePopup + return
    │   └── word 不为空 → ↓
    │
    └── 后续原有逻辑（关键词+模板+对象+DB名称）
```

#### DetectCrossDbContextV2 检测逻辑

```csharp
/// <summary>
/// 增强版跨库上下文检测。
/// 从光标位置向前查找 "DBName." 或 "[DBName]." 模式。
/// Schema 名也视为单词字符的一部分（支持 "DBName.dbo." 的二级检测）。
/// </summary>
private (string? dbName, string? schemaName, string objectPrefix) DetectCrossDbContextV2(IWpfTextView textView)
{
    var caret = textView.Caret.Position.BufferPosition;
    var line = caret.GetContainingLine();
    var lineText = line.Extent.GetText();
    var column = caret.Position - line.Start.Position;

    if (column <= 0) return (null, null, "");

    int pos = column - 1;

    // Step 1: 跳过当前单词（光标前的部分）
    int wordEnd = pos;
    while (pos >= 0 && IsWordChar(lineText[pos])) pos--;

    string currentWord = lineText.Substring(pos + 1, wordEnd - pos);

    // Step 2: 检查点号前是否有内容
    if (pos >= 0 && lineText[pos] == '.')
    {
        int firstDot = pos;

        // Step 3: 读取点号前的单词（可能是 schema 名或 DB 名）
        pos--;
        int nameEnd = pos;
        while (pos >= 0 && IsWordChar(lineText[pos])) pos--;
        string nameBeforeDot = lineText.Substring(pos + 1, nameEnd - pos);

        // 检查 [DBName] 模式
        if (pos >= 0 && lineText[pos] == ']')
        {
            return ExtractDbNameFromBracket(lineText, pos, firstDot, currentWord);
        }

        // Step 4: 检查是否是 DBName.Schema 模式（有两个点号）
        if (pos >= 0 && lineText[pos] == '.')
        {
            int secondDot = pos;
            pos--;
            int dbNameEnd = pos;
            while (pos >= 0 && IsWordChar(lineText[pos])) pos--;
            string dbName = lineText.Substring(pos + 1, dbNameEnd - pos);

            if (IsKnownDatabase(dbName))
            {
                // 模式: DBName.Schema.ObjectPrefix
                // dbName = "MESDATA", nameBeforeDot = "dbo", currentWord = "Use"
                return (dbName, nameBeforeDot, currentWord);
            }
        }

        // Step 5: 检查是否是 DBName.Object 模式（一个点号）
        if (IsKnownDatabase(nameBeforeDot))
        {
            // 模式: DBName.ObjectPrefix （此时 nameBeforeDot 是 DB 名）
            return (nameBeforeDot, null, currentWord);
        }

        // Step 6: 检查是否是 Schema.Object 模式（无跨库）
        if (IsKnownSchema(nameBeforeDot))
        {
            // 模式: Schema.ObjectPrefix，在当前 DB 中搜索
            return (_currentDbName, nameBeforeDot, currentWord);
        }
    }

    return (null, null, "");
}
```

#### HandleCrossDbCompletion 分支逻辑

```csharp
private void HandleCrossDbCompletion(string dbName, string? schemaName, string objectPrefix)
{
    if (_cacheService == null) return;

    // 懒惰加载目标 DB 的缓存（如果尚未加载）
    _ = Task.Run(() => _cacheService.EnsureDatabaseLoadedAsync(dbName));

    var contextKeyword = GetContextBeforeDbName(_activeTextView, ...);
    var category = GetCompletionCategory(contextKeyword);

    if (schemaName != null)
    {
        // 已指定 Schema → 搜索该 Schema 下的对象
        var matches = _cacheService.GetMatchingObjectsForDbAndSchema(dbName, schemaName, objectPrefix, category);
        ShowOrUpdatePopup(matches, objectPrefix);
    }
    else if (category == CompletionCategory.ProceduresAndFunctions)
    {
        // EXEC 场景 → 跳过 Schema，直接显示该 DB 的 SP/函数
        var matches = _cacheService.GetAllObjectsOfTypesForDb(dbName, "P", "FN", "IF", "TF");
        if (!string.IsNullOrEmpty(objectPrefix))
            matches = matches.Where(m => FuzzyMatch(objectPrefix, m.FilterText) >= 0).ToList();
        ShowOrUpdatePopup(matches, objectPrefix);
    }
    else
    {
        // FROM/JOIN 场景 → 显示 Schema 列表
        var schemaMatches = _cacheService.GetSchemaCandidatesForDb(dbName, objectPrefix);
        ShowOrUpdatePopup(schemaMatches, objectPrefix);
    }
}
```

---

### 4.4 M1.4 — DB 名称候选去重

#### 判定逻辑

在 `AddDbNameCandidates` 方法中增加去重检测：

```csharp
private void AddDbNameCandidates(string word, List<CompletionCandidate> allMatches)
{
    if (_cacheService == null || string.IsNullOrEmpty(word)) return;

    var dbCandidates = _cacheService.GetDatabaseCandidates();
    foreach (var dbCandidate in dbCandidates)
    {
        // ★ 去重检测：如果输入的 word 完全匹配该 DB 名称，跳过
        if (string.Equals(word, dbCandidate.FilterText, StringComparison.OrdinalIgnoreCase))
            continue;

        var score = CompletionCandidate.FuzzyMatch(word, dbCandidate.FilterText);
        if (score >= 0)
        {
            dbCandidate.MatchScore = score;
            dbCandidate.ItemType = CompletionItemType.Database;
            allMatches.Add(dbCandidate);
        }
    }
}
```

**去重规则**：
- `word` = "MESDATA", `FilterText` = "MESDATA" → **完全匹配** → 跳过（不显示 "MESDATA."）
- `word` = "MES", `FilterText` = "MESDATA" → **部分匹配** → 显示（得分 500+）
- `word` = "MESDATA.", `FilterText` = "MESDATA" → 不在候选逻辑中（跨库模式下不调用 AddDbNameCandidates）

---

### 4.5 M2 — Schema 补全

#### M2.1 Schema 缓存

**SQL 查询**
```sql
SELECT name FROM [@DBName].sys.schemas
WHERE name NOT LIKE 'db_%'
ORDER BY name
```

**缓存加载时机**
| 时机 | 行为 |
|------|------|
| 服务初始化 `TryAutoLoadAsync` | 当前 DB 加载对象时，同步加载该 DB 的 Schema 列表 |
| 跨库懒加载 `EnsureDatabaseLoadedAsync` | 加载目标 DB 对象时，同步加载该 DB 的 Schema 列表 |
| 缓存刷新 `RefreshCacheForCurrentConnectionAsync` | 刷新对象时，同步刷新 Schema 列表 |

**降级策略**
- `sys.schemas` 查询失败 → 使用默认列表：`["dbo", "sys", "guest", "INFORMATION_SCHEMA"]`
- 记录警告日志但**不阻塞**对象缓存加载

#### M2.2 Schema 候选生成

```csharp
public List<CompletionCandidate> GetSchemaCandidatesForDb(string dbName, string prefix)
{
    var cacheKey = $"{_currentServer}.{dbName}";
    if (!_cache.TryGetValue(cacheKey, out var cache))
        return new List<CompletionCandidate>();

    var candidates = new List<CompletionCandidate>();
    foreach (var schema in cache.Schemas)
    {
        // 筛除系统 schema
        if (schema.StartsWith("db_")) continue;

        int score = string.IsNullOrEmpty(prefix) ? 0
            : CompletionCandidate.FuzzyMatch(prefix, schema);

        if (score >= 0)
        {
            candidates.Add(new CompletionCandidate
            {
                DisplayText = schema,
                FilterText = schema,
                TypeTag = "模式",
                InsertText = schema + ".",
                ItemType = CompletionItemType.Schema,
                MatchScore = score,
            });
        }
    }

    return candidates.OrderByDescending(c => c.MatchScore)
        .ThenBy(c => c.FilterText).ToList();
}
```

#### M2.3 排序优先级

| 场景 | 优先级排序 |
|------|-----------|
| FROM/JOIN `DBName.` + 输入前缀 | Schema匹配(0) > 对象匹配(1) > 关键词(2) > 模板(3) |
| EXEC `DBName.` + 输入前缀 | SP/函数匹配(0) > Schema匹配(1) > 关键词(2) > 模板(3) |
| `DBName.Schema.` + 输入前缀 | 对象匹配(0) > 关键词(1) > 模板(2) |

---

### 4.6 M3.1 — 表结构缓存（列信息）

#### SQL 查询
```sql
SELECT
    c.name AS ColumnName,
    TYPE_NAME(c.user_type_id) AS DataType,
    c.max_length,
    c.precision,
    c.scale,
    c.is_nullable,
    c.is_identity,
    OBJECT_DEFINITION(c.default_object_id) AS DefaultValue,
    ep.value AS Description
FROM [@DBName].sys.columns c
LEFT JOIN [@DBName].sys.extended_properties ep
    ON ep.major_id = c.object_id
    AND ep.minor_id = c.column_id
    AND ep.name = 'MS_Description'
WHERE c.object_id = OBJECT_ID('[@Schema].[@TableName]')
ORDER BY c.column_id
```

> **注意**：`COLUMN_DEFAULT` 函数在某些 SQL Server 版本中不可用，改用 `OBJECT_DEFINITION(c.default_object_id)` 获取默认值定义。

#### 缓存策略

| 策略 | 说明 |
|------|------|
| **加载时机** | 与表对象缓存同时加载，在 `FetchAndCacheAsync` 中作为一个批量查询 |
| **并发控制** | 使用 `SemaphoreSlim(3)` 限制并发连接数 |
| **懒加载** | 仅在用户查看表结构（单击）或触发字段补全（`TableA.`）时才需要，可先仅缓存列名+类型，详细列信息按需加载 |
| **超时** | 单个表的列信息查询超时为 5 秒 |
| **失败处理** | 列信息加载失败 → 该表 `Columns` 为 null → 字段补全不显示该表 |

#### 缓存持久化（按DB分文件存储）

**改为按DB分文件存储**（不共用同一个文件），每个DB的缓存独立存为一个文件。

**缓存目录**：`{部署目录}\cache\`

**文件命名规则**：
```
{Server机器名或IP}_{Database名}_object_cache.json
```

其中：
- `{Server}` 中的 `.` 替换为 `_`（避免文件名中的非法字符 `.`）
- `:` 也替换为 `_`（针对 IPv6 或带端口号的连接字符串）
- `{Database}` 保持不变

| Server | Database | 文件名 |
|--------|----------|--------|
| `192.168.1.100` | `MESDATA` | `192_168_1_100_MESDATA_object_cache.json` |
| `192.168.1.100` | `Northwind` | `192_168_1_100_Northwind_object_cache.json` |
| `SQLSRV01\INSTANCE1` | `ReportDB` | `SQLSRV01_INSTANCE1_ReportDB_object_cache.json` |
| `localhost` | `tempdb` | `localhost_tempdb_object_cache.json` |

**加载逻辑**：

```csharp
/// <summary>
/// 根据 Server 和 Database 构建缓存文件名路径。
/// </summary>
private string GetCacheFilePath(string server, string database)
{
    var cacheDir = Path.Combine(
        Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!,
        "cache");

    if (!Directory.Exists(cacheDir))
        Directory.CreateDirectory(cacheDir);

    // 将 Server 中的非法文件名字符替换为下划线
    var safeServer = server
        .Replace('.', '_')
        .Replace(':', '_')
        .Replace('\\', '_');

    return Path.Combine(cacheDir, $"{safeServer}_{database}_object_cache.json");
}
```

**读写逻辑**：

```csharp
/// <summary>
/// 从按DB分文件的缓存中读取指定 Database 的缓存。
/// </summary>
private DatabaseObjectCache? LoadCacheFromFile(string server, string database)
{
    var filePath = GetCacheFilePath(server, database);
    if (!File.Exists(filePath))
        return null;

    try
    {
        var json = File.ReadAllText(filePath, Encoding.UTF8);
        return JsonConvert.DeserializeObject<DatabaseObjectCache>(json);
    }
    catch (Exception ex)
    {
        _logger.Warning(ex, $"[Cache] 读取缓存文件失败: {filePath}，将重建缓存");
        return null;
    }
}

/// <summary>
/// 将指定 Database 的缓存保存到按DB分文件中。
/// </summary>
private void SaveCacheToFile(DatabaseObjectCache cache)
{
    var filePath = GetCacheFilePath(cache.Server, cache.Database);
    var json = JsonConvert.SerializeObject(cache, Formatting.Indented);
    File.WriteAllText(filePath, json, Encoding.UTF8);
}
```

**启动时加载策略**：
1. 优先尝试从本地分文件加载当前连接 DB 的缓存文件（快速启动）
2. 如果本地文件不存在或已过期（超过 24 小时），则在后台异步重建
3. 其他 DB 的缓存按需加载（用户输入触发跨库补全时）
4. 不再在启动时全量加载所有 DB 的缓存文件

**优势对比**：

| 特性 | 单文件方案（旧） | 分文件方案（新） |
|------|----------------|----------------|
| 启动速度 | 需全部加载 | 仅加载当前 DB，快速启动 |
| 文件体积 | 50个DB → 200MB 单文件 | 每 DB 2-4MB，按需读取 |
| 并发安全 | 多线程写同一文件需锁 | 不同 DB 写不同文件，无竞争 |
| 清理灵活性 | 删除整个缓存 | 可单独删除某个 DB 的缓存 |
| 加载粒度 | 全量加载 | 按 DB 名精确加载 |

#### 缓存文件大小预估

| 内容 | 平均大小 |
|------|---------|
| 对象列表（1000个对象） | ~200KB |
| Schema 列表（10个 schema） | ~0.5KB |
| 列信息（20列/表 × 1000表） | ~1-2MB |
| SP 参数（5参/SP × 200 SP） | ~50KB |
| 单 DB 缓存总计 | ~1.5-2.5MB |
| 50 个 DB 总计（全量） | ~75-125MB（分文件存储） |

---

### 4.7 M3.3 — 表结构信息弹窗 UI

#### 交互设计

```
┌─────────────────────────────────────┐
│  [dbo].[Users]       表  ← 补全弹窗  │
│  [dbo].[Orders]      表              │
│  [dbo].[Products]    表   ← 鼠标悬停 │
│  ┌─────────────────────────┐        │
│  │ CREATE TABLE [dbo].[    │  ← 右侧弹窗
│  │ Products] (             │        │
│  │   [Id] INT NOT NULL,    │        │
│  │   [Name] NVARCHAR(100)  │        │
│  │   [Price] DECIMAL(18,2) │        │
│  │ )                       │        │
│  └─────────────────────────┘        │
└─────────────────────────────────────┘
```

#### 触发方式
- **鼠标悬浮**：项目上停留 500ms 后自动弹出结构信息（需要防抖，避免快速掠过时闪烁）
- **单击选中后快捷键**：选中项目后按 `Ctrl+Space` 弹出结构信息
- **暂停实现**：Phase 2 仅实现表结构缓存，Phase 3 再实现 UI 展示

#### 信息弹窗规格
| 属性 | 值 |
|------|-----|
| 定位 | 补全弹窗右侧 4px 偏移 |
| 宽度 | 自适应，最小 300px，最大 600px |
| 高度 | 自适应，最大 400px（超出滚动） |
| 字体 | Consolas, 11pt |
| 背景色 | #FAFAFA |
| 边框 | 1px solid #D0D0D0, CornerRadius=3 |
| 阴影 | DropShadow BlurRadius=6, Opacity=0.3 |
| 关闭方式 | 点击外部区域 或 ESC |

---

### 4.8 M4 — 字段补全

#### M4.1 表名上下文检测

**检测场景**：
```
SELECT * FROM Users WHERE Users.       → 表名 "Users"
SELECT * FROM Users U WHERE U.         → 别名 "U" → 解析为 "Users"
FROM Orders O JOIN Users U ON ... WHERE O.  → 别名 "O" → "Orders"
```

**检测流程**：
```
用户输入 "TableName." 或 "Alias."
    │
    ├── DetectCrossDbContextV2 未检测到跨库
    │   └── ↓
    │
    ├── 读取光标前的单词（点号前的内容）
    │
    ├── [检查1] 是否是当前 DB 中的表名？
    │   ├── 是 → 使用该表的 Columns → 生成字段候选
    │   └── 否 → ↓
    │
    ├── [检查2] 是否是已声明的别名？
    │   ├── 是 → 解析为真实表名 → 使用该表的 Columns
    │   └── 否 → ↓
    │
    └── 不是表名或别名 → 不触发字段补全，走原有逻辑
```

#### M4.2 别名解析

**正则表达式**：
```regex
\b(?:FROM|JOIN)\s+\[?(\w+(?:\.\w+)?)\]?(?:\s+AS)?\s+(\w+)\b
```

**解析逻辑**：
```csharp
private Dictionary<string, string> ResolveAliasMap(IWpfTextView textView)
{
    var aliasMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    var snapshot = textView.TextBuffer.CurrentSnapshot;

    // 从第一行到光标所在行，搜索 FROM/JOIN 别名声明
    var caretLine = snapshot.GetLineFromPosition(textView.Caret.Position.BufferPosition.Position);
    for (int i = 0; i <= caretLine.LineNumber; i++)
    {
        var lineText = snapshot.GetLineFromLineNumber(i).GetText();
        var matches = Regex.Matches(lineText, @"\b(FROM|JOIN)\s+\[?(\w+(?:\.\w+)?)\]?(?:\s+AS)?\s+(\w+)\b",
            RegexOptions.IgnoreCase);

        foreach (Match match in matches)
        {
            string tableName = match.Groups[2].Value;  // 表名
            string alias = match.Groups[3].Value;       // 别名

            // 处理方括号包裹的表名: [dbo].[Users] → dbo.Users
            tableName = tableName.Replace("[", "").Replace("]", "");

            // 如果表名不含 Schema，添加默认 Schema
            if (!tableName.Contains('.'))
                tableName = "dbo." + tableName;

            aliasMap[alias] = tableName;
        }
    }
    return aliasMap;
}
```

#### M4.3 字段补全弹窗

```csharp
private void HandleFieldOrAliasCompletion(IWpfTextView textView, string wordBeforeDot)
{
    string? tableName = null;

    // 检查1：是否是表名
    tableName = _cacheService?.ResolveTableName(wordBeforeDot);

    // 检查2：是否是别名
    if (tableName == null)
    {
        var aliasMap = ResolveAliasMap(textView);
        if (aliasMap.TryGetValue(wordBeforeDot, out var resolvedTable))
            tableName = resolvedTable;
    }

    if (tableName == null) return; // 不触发字段补全

    // 获取字段列表
    var columns = _cacheService?.GetTableColumns(tableName, "");
    if (columns == null || columns.Count == 0) return;

    var allMatches = columns.Select(c => new CompletionCandidate
    {
        DisplayText = c.Name,
        FilterText = c.Name,
        TypeTag = "字段",
        InsertText = c.Name + " ",
        ItemType = CompletionItemType.Field,
        MatchScore = 0,
    }).ToList();

    ShowOrUpdatePopup(allMatches, "");
}
```

---

### 4.9 M5 — SP/函数参数补全

#### M5.1 SP参数缓存

**SQL 查询**
```sql
SELECT
    p.name AS ParameterName,
    TYPE_NAME(p.user_type_id) AS DataType,
    p.max_length,
    p.precision,
    p.scale,
    p.is_output,
    p.has_default_value,
    p.default_value
FROM [@DBName].sys.parameters p
WHERE p.object_id = OBJECT_ID('[@Schema].[@ProcName]')
ORDER BY p.parameter_id
```

#### M5.2 参数列表展开

在 `ExpandSelectedTemplate` 中，当展开一个 SP/函数时：

```csharp
private void ExpandSelectedTemplate()
{
    // ... 节流、获取选中项等前置逻辑 ...

    if (candidate.ItemType == CompletionItemType.DatabaseObject
        && candidate.Source is DatabaseObjectInfo objInfo
        && (objInfo.TypeCode == "P" || objInfo.TypeCode == "FN" ||
            objInfo.TypeCode == "IF" || objInfo.TypeCode == "TF"))
    {
        // SP/函数：检查参数列表
        if (objInfo.Parameters != null && objInfo.Parameters.Count > 0)
        {
            var paramNames = objInfo.Parameters
                .Where(p => !p.IsOutput)  // 排除 OUTPUT 参数
                .Select(p => p.Name);

            if (objInfo.TypeCode == "FN" || objInfo.TypeCode == "TF")
            {
                // 函数：用圆括号包裹参数
                insertText = $"{objInfo.Name}({string.Join(", ", paramNames)})";
                // 光标定位在第一个参数后
                // ... cursor targeting logic ...
            }
            else
            {
                // SP：空格分隔参数
                insertText = $"{objInfo.Name} {string.Join(", ", paramNames)}";
                // 光标定位在第一个参数名后
                // ... cursor targeting logic ...
            }
        }
        else
        {
            // 无参数：SP名 + 空格
            insertText = objInfo.Name + " ";
        }
    }

    // ... 执行 DoExpand ...
}
```

**光标定位逻辑**：展开后光标定位在第一个参数名之后（不是参数名之前），方便用户直接输入值。

```
例: "dbo.GetUserBy @Id, @Name, @Status"
                        ↑ 光标在这里（@Id 之后）
```

---

### 4.10 M6 — 打开脚本自动注释

#### M6.1 新增菜单命令

在 `SSMSBoostPlusPackage.cs` 中新增菜单项：

```xml
<Button guid="guidSSMSBoostPlusCmdSet" id="cmdidOpenScript" priority="0x0100">
    <MenuText>打开脚本</MenuText>
    <ButtonText>打开脚本</ButtonText>
    <ToolTipText>打开 .sql 脚本文件并自动注释 ALTER/CREATE 语句</ToolTipText>
</Button>
```

#### M6.2 处理流程

```csharp
private async Task OpenScriptAndAutoCommentAsync()
{
    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

    // 1. 打开文件选择对话框
    var dlg = new OpenFileDialog
    {
        Filter = "SQL 脚本 (*.sql)|*.sql|所有文件 (*.*)|*.*",
        Title = "打开 SQL 脚本",
        Multiselect = false,
    };

    if (dlg.ShowDialog() != true) return;

    // 2. 读取文件内容
    var content = await File.ReadAllTextAsync(dlg.FileName);
    var lines = content.Split(new[] { "\r\n", "\n", "\r" }, StringSplitOptions.None);

    // 3. 自动注释 ALTER/CREATE 行
    var modifiedLines = new List<string>();
    bool hasModifications = false;

    foreach (var line in lines)
    {
        var trimmed = line.TrimStart();
        if ((trimmed.StartsWith("CREATE ", StringComparison.OrdinalIgnoreCase) ||
             trimmed.StartsWith("ALTER ", StringComparison.OrdinalIgnoreCase)) &&
            !line.TrimStart().StartsWith("--"))
        {
            // 确保不是已有注释
            modifiedLines.Add("-- " + line);
            hasModifications = true;
        }
        else
        {
            modifiedLines.Add(line);
        }
    }

    // 4. 创建新文档并插入内容
    var dte = await _package.GetServiceAsync(typeof(DTE)) as DTE;
    if (dte == null) return;

    var document = dte.ItemOperations.NewFile("General\\Text File", dlg.FileName, Constants.vsViewKindTextView);
    var textDocument = (TextDocument)document.Object("TextDocument");
    var editPoint = textDocument.StartPoint.CreateEditPoint();
    editPoint.Insert(string.Join(Environment.NewLine, modifiedLines));

    // 5. 光标定位到文件开头
    editPoint.StartOfDocument();

    _logger.Information($"[AutoComment] 已打开脚本 '{Path.GetFileName(dlg.FileName)}'，注释了 {modifiedLines.Count - lines.Count} 行");
}
```

#### 注释规则矩阵

| 输入行 | 匹配 | 输出行 |
|--------|------|--------|
| `CREATE PROCEDURE dbo.Test AS` | 是 | `-- CREATE PROCEDURE dbo.Test AS` |
| `  ALTER TABLE dbo.Users ADD Col1` | 是 | `--   ALTER TABLE dbo.Users ADD Col1` |
| `-- CREATE PROCEDURE dbo.Test` | 否（已有注释） | `-- CREATE PROCEDURE dbo.Test` |
| `  -- ALTER TABLE` | 否（已有注释） | `  -- ALTER TABLE` |
| `SELECT * FROM Users` | 否 | `SELECT * FROM Users` |
| `GO` | 否 | `GO` |
| `CREATE #tempTable` | 是（临时表） | `-- CREATE #tempTable` |
| `CREATE TABLE #temp` | 是 | `-- CREATE TABLE #temp` |

---

## 5. 数据库查询权限清单

| 查询 | 需要的权限 | 适用场景 |
|------|-----------|---------|
| `sys.databases WHERE database_id > 4` | `VIEW ANY DATABASE` 或 `sysadmin` | 获取所有 DB 名称 |
| `[DB].sys.schemas` | 对该 DB 的 `VIEW DEFINITION` | 获取 Schema 列表 |
| `[DB].sys.objects` | 对该 DB 的 `VIEW DEFINITION` | 获取对象列表 |
| `[DB].sys.columns` | 对该 DB 的 `VIEW DEFINITION` | 获取列信息 |
| `[DB].sys.parameters` | 对该 DB 的 `VIEW DEFINITION` | 获取 SP 参数 |
| `[DB].sys.extended_properties` | `PUBLIC` 角色即可 | 获取列描述 |

---

## 6. 状态机：OnCharTyped 完整决策树

```
OnCharTyped 入口
│
├─ 1. DetectCrossDbContextV2
│   ├─ 检测到跨库模式 (dbName != null)
│   │   ├─ 获取上下文关键词 (FROM/EXEC/JOIN/SELECT...)
│   │   ├─ schemaName != null → 已有 Schema
│   │   │   └─ 搜索该 Schema 下匹配对象 + 显示弹窗
│   │   ├─ category == ProceduresAndFunctions (EXEC)
│   │   │   └─ 跳过Schema → 直接搜索该 DB 的 SP/函数 → 显示弹窗
│   │   └─ category == TablesAndViews/KeywordsAndTemplates
│   │       └─ 显示Schema列表 + 显示弹窗
│   │
│   └─ 未检测到跨库模式
│       └─ ↓ 进入常规逻辑
│
├─ 2. GetWordBeforeCaret
│   ├─ word 为空
│   │   ├─ _hasSpaceTrigger && _isPopupVisible → 不关闭弹窗（保持分类）
│   │   ├─ 刚隐藏弹窗 (<200ms) → 不自动重开
│   │   └─ 其他 → HidePopup + return
│   │
│   └─ word 不为空 → ↓
│
├─ 3. 检测表名/别名字段上下文
│   ├─ 检测到 "TableName." 或 "Alias." 模式
│   │   └─ 字段补全分支 → 从缓存获取字段列表 → 显示弹窗 + return
│   └─ 未检测到 → ↓
│
├─ 4. 获取上下文关键词 contextKeyword + 确定分类 category
│
├─ 5. 构建候选项列表
│   ├─ 关键词 (前缀索引快速匹配)
│   ├─ 模板 (缓存全量扫描)
│   ├─ 数据库对象 (按 category 过滤)
│   │   ├─ TablesAndViews → 表+视图
│   │   └─ ProceduresAndFunctions → SP+函数
│   └─ DB 名称候选 (去重后)
│
├─ 6. 排序 (MatchScore → category优先级 → FilterText)
│
└─ 7. ShowOrUpdatePopup
    ├─ 有匹配项 → 显示/更新弹窗
    └─ 无匹配项 → HidePopup
```

---

## 7. 缓存生命周期

### 7.1 缓存键设计

| 键 | 格式 | 说明 |
|---|------|------|
| 内存缓存 Key | `"{Server}_{Database}"` | 运行时 `ConcurrentDictionary` 的键（Server中的 `.` 替换为 `_`） |
| 文件缓存 Key | 按文件名 `{safeServer}_{Database}_object_cache.json` | 每个 DB 独立一个文件 |
| Schema 列表 | 存储在 `DatabaseObjectCache.Schemas` 中 | 内嵌在单个 DB 缓存文件中 |
| 别名映射 | 运行时内存计算，不缓存 | — |

### 7.2 缓存目录结构

```
{部署目录}/
├── Logs/
├── cache/                          ← 新增缓存目录
│   ├── 192_168_1_100_MESDATA_object_cache.json
│   ├── 192_168_1_100_Northwind_object_cache.json
│   ├── SQLSRV01_INSTANCE1_ReportDB_object_cache.json
│   └── ...
├── SSMSBoostPlus.SSMS.dll
├── settings.json
└── ...
```

### 7.3 加载顺序

```
服务启动/连接切换
    │
    ├── 1. 获取当前连接信息 (Server, Database, ConnectionString)
    │
    ├── 2. 设置 _currentServer
    │
    ├── 3. [新增] 尝试从本地文件读取当前 DB 的缓存
    │   ├── 文件存在 && 未过期 (<24h) → 直接加载到内存 ✅ 快速启动
    │   ├── 文件存在但过期 → 后台异步重建
    │   └── 文件不存在 → 后台异步拉取
    │
    ├── 4. 查询当前服务器所有 DB 名称 (> database_id 4)
    │
    ├── 5. 仅对其他 DB 做名称列表缓存（不立即加载内容）
    │
    ├── 6. 如果是后台异步重建模式：并行拉取（并发数=3）
    │   ├── 6a. 查询对象列表 (sys.objects)
    │   ├── 6b. 查询 Schema 列表 (sys.schemas)
    │   ├── 6c. [新增] 查询列信息 (sys.columns) — 仅当前 DB
    │   ├── 6d. [新增] 查询 SP 参数 (sys.parameters) — 仅当前 DB
    │   └── 6e. 保存到分文件 `{Server}_{Database}_object_cache.json`
    │
    ├── 7. 构建内存前缀索引
    │
    └── 8. 跨库懒加载：用户输入 `DBName.` 时触发
        └── 检查该 DB 的分文件是否存在
            ├── 存在 → 从文件加载到内存
            └── 不存在 → 从数据库拉取并保存到分文件
```

### 7.4 缓存有效期

| 缓存 | 有效期 | 刷新时机 |
|------|--------|---------|
| DB 名称列表 | 单次会话 | 切换连接时刷新 |
| 对象列表（内存） | 单次会话 | 手动刷新命令 |
| 对象列表（本地文件） | 24 小时 | 过期后后台异步重建 |
| Schema 列表 | 单次会话 | 随对象列表一起刷新 |
| 列信息 | 单次会话 | 随对象列表一起刷新 |
| SP 参数 | 单次会话 | 随对象列表一起刷新 |

---

## 8. 测试要点

### 8.1 单元测试新增用例

| 测试模块 | 测试用例 | 覆盖场景 |
|----------|---------|---------|
| M1.1 选区 | `ExpandSelectedTemplate_WhenTextSelected_DeletesSelection` | 选中文本 + Enter补全 |
| M1.1 选区 | `InsertCharAndRefresh_WhenTextSelected_DeletesSelection` | 选中文本 + 键入字符 |
| M1.2 ESC | `Exec_WhenPopupVisibleAndEscPressed_ClosesPopup` | ESC 关闭弹窗 (VSStd2K) |
| M1.2 ESC | `Exec_WhenPopupVisibleAndEscPressed_DoesNotReopen` | ESC 关闭后不自动重开 |
| M1.3 跨库 | `OnCharTyped_WhenDotAfterDbName_ShowsSchemaList` | "DBName." → Schema |
| M1.3 跨库 | `OnCharTyped_WhenExecDotAfterDbName_ShowsProcedures` | "EXEC DBName." → SP |
| M1.4 去重 | `AddDbNameCandidates_WhenWordEqualsDbName_SkipsDbName` | 输入完整DB名 → 去重 |
| M2 Schema | `GetSchemaCandidatesForDb_ReturnsSchemas` | Schema 候选生成 |
| M2 Schema | `GetSchemaCandidatesForDb_WhenQueryFails_ReturnsDefault` | Schema 查询失败降级 |
| M3 列缓存 | `FetchColumnsAsync_ReturnsColumnInfo` | 列信息查询 |
| M4 别名 | `ResolveAliasMap_ReturnsCorrectMapping` | FROM/JOIN 别名解析 |
| M4 字段补全 | `HandleFieldCompletion_WhenTableName_ShowsFields` | 表名字段补全 |
| M5 SP参数 | `BuildParamInsertText_WhenSpHasParams_ReturnsParamList` | SP参数列表构建 |
| M6 注释 | `AutoComment_WhenLineStartsWithCreate_AddsComment` | CREATE 行注释 |
| M6 注释 | `AutoComment_WhenAlreadyCommented_SkipsLine` | 已有注释不重复 |

### 8.2 集成测试场景

| 场景 | 测试数据 | 预期结果 |
|------|---------|---------|
| 连接有 50 个 DB 的服务器 | 模拟 50 个 DB 的元数据 | 后台异步加载，UI 不卡顿 |
| 跨库补全 + 字段补全接力 | "FROM MESDATA.dbo.Users U WHERE U." | U 字段弹窗 |
| EXEC 跨库 + SP 参数展开 | "EXEC OtherDB.dbo.GetUserBy" | 展开为含参数的完整语句 |
| 打开含 10 个 CREATE 的脚本 | 10 个 CREATE 语句 | 全部自动注释 |

---

## 9. 配置项

| 配置项 | 默认值 | 说明 |
|--------|--------|------|
| `EnableSchemaCompletion` | `true` | 是否启用 Schema 补全 |
| `EnableFieldCompletion` | `true` | 是否启用字段补全 |
| `EnableSpParamCompletion` | `true` | 是否启用 SP/函数参数补全 |
| `EnableAutoComment` | `true` | 是否启用打开脚本自动注释 |
| `SchemaCompletionMaxItems` | `20` | Schema 补全最大显示数 |
| `FieldCompletionMinChars` | `1` | 字段补全触发最小字符数 |

配置存储在 `{部署目录}\settings.json` 中。

---

## 10. 异常处理矩阵

| 异常场景 | 表现 | 日志级别 | 用户提示 |
|----------|------|---------|---------|
| `sys.schemas` 查询超时 | 使用默认 Schema 列表 | Warning | 无（静默降级） |
| `sys.columns` 查询失败 | 该表无列信息，字段补全不显示该表 | Warning | 无 |
| `sys.parameters` 查询失败 | 该 SP 无参数补全 | Warning | 无 |
| 别名解析正则不匹配 | 不走字段补全分支 | Debug | 无（静默降级） |
| 缓存分文件损坏 | 删除该 DB 的缓存文件，后台重新拉取 | Error | 弹出通知 "MESDATA 缓存已重建" |
| 缓存目录创建失败 | 不使用本地缓存，仅内存缓存 | Warning | 无（静默降级） |
| 缓存文件写入权限不足 | 不使用本地缓存，仅内存缓存 | Warning | 无（静默降级） |
| 跨库补全时目标 DB 分文件不存在 | 后台异步拉取并保存 | Info | 无（静默加载） |
| 自动注释文件太大（>5MB） | 不执行注释 | Warning | 弹出提示 "文件过大，跳过自动注释" |
| SSMS 关闭时正在加载缓存 | 取消加载任务 | Debug | 无 |
