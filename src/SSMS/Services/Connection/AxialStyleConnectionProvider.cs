using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.Shell;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;

namespace SSMSBoostPlus.SSMS.Services.Connection;

/// <summary>
/// SSMS 22 连接 Provider — 多条独立策略绕过 ServiceCache 内部状态异常。
/// 
/// 背景：SSMS 22 中 ServiceCache.ScriptFactory 内部抛 NullReferenceException、
/// ServiceCache.ServiceProvider 为 null，导致 AxialSqlTools 的两条主路径均不可用。
/// 此 Provider 改用 DTE 窗口标题解析、ObjectExplorerToolWindow 深度探索、
/// VsServiceProvider、ConnectionStore 等替代路径。
/// 
/// 策略优先级（按可行性和成功率）：
/// A. DTE 窗口标题解析 — 从查询窗口标题提取 Server.Database（最可靠）
/// B. ObjectExplorerToolWindow 深度探索 — 通过 DTE 获取 OE 窗口，反射提取选中节点连接
/// C. VsServiceProvider — 通过 ServiceCache.VsServiceProvider 获取 ObjectExplorerService
/// D. ConnectionStore — 探索已缓存连接
/// E. ScriptFactory — 兜底（已知在 SSMS 22 中可能抛 NRE）
/// 
/// 仅在 SSMS 22+ 上激活，非 22 版本自动跳过回退到反射 Provider。
/// </summary>
public class AxialStyleConnectionProvider : IConnectionInfoProvider
{
    private readonly IServiceProvider _serviceProvider;
    private readonly ILoggerService? _logger;
    private readonly ConnectionCache _connectionCache;

    private static Type? _serviceCacheType;
    private static readonly object _typeLock = new();

    // 策略A 解析出的窗口标题服务器名（用于后续策略 B~E 的服务器一致性校验）
    // 场景：用户切换到新服务器（缓存无凭据）时，策略B 从 Object Explorer 可能拿到
    // 其他服务器（OE 节点未同步切换）的连接 → 必须拒绝，否则补全/缓存使用错误服务器
    private string? _windowCaptionServer;
    private string? _windowCaptionUser;

    /// <summary>
    /// 最近一次从窗口标题解析出的服务器名（静态，供 ConnectionFacade 跨 provider 一致性校验）。
    /// 用户切换连接后，所有 provider 返回的连接必须与此一致，否则视为获取失败。
    /// </summary>
    public static string? LastWindowCaptionServer { get; private set; }

    public string ProviderName => "Axial-Style Connection Provider (Recommended)";
    public int Priority => -20;
    public bool IsAvailable => SmartAssemblyLoader.GetDetectedVersion() >= SSMSVersion.V22;

    public AxialStyleConnectionProvider(IServiceProvider serviceProvider, ILoggerService? logger = null, ConnectionCache? connectionCache = null)
    {
        _serviceProvider = serviceProvider ?? throw new ArgumentNullException(nameof(serviceProvider));
        _logger = logger;
        _connectionCache = connectionCache ?? new ConnectionCache(logger);
    }

    public async Task<ConnectionInfo?> GetConnectionInfoAsync()
    {
        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

        if (!IsAvailable)
        {
            _logger?.Debug("[AxialStyle] Not available (SSMS < 22)");
            return null;
        }

        try
        {
            _logger?.Information("[AxialStyle] Attempting connection retrieval with multi-path strategy...");
            SmartAssemblyLoader.EnsureAssembliesLoaded(_logger);

            // 策略A: DTE 窗口标题解析 — 最可靠，不需要 ServiceCache
            // 成功后内部会记录 _windowCaptionServer；失败（如新服务器缓存未命中）也会记录，供一致性校验
            var connInfo = TryGetFromWindowCaption();
            if (connInfo != null) { _logger?.Information("[AxialStyle] ✅ Got connection from Window Caption"); TryUpdateCache(connInfo); return connInfo; }

            // ★ 服务器一致性参考（策略A 解析出的窗口标题服务器名）
            // 场景：用户切换到新服务器（172.16.50.167），缓存无凭据 → 策略A 返回 null
            // 但 Object Explorer 可能仍停留在旧服务器节点（172.16.183.25），
            // 策略B~E 会错误返回旧服务器的连接 → 必须拒绝，否则补全/缓存使用错误服务器且无任何提示
            string? windowServer = _windowCaptionServer;
            string? windowUser = _windowCaptionUser;
            if (!string.IsNullOrEmpty(windowServer))
            {
                _logger?.Debug($"[AxialStyle] Window caption server reference: {windowServer} (user={windowUser ?? ""})");
            }

            // 策略B: ObjectExplorerToolWindow 深度探索 — 绕过 ServiceProvider==null
            connInfo = ValidateServerMatch(TryGetFromOEToolWindow(), windowServer);
            if (connInfo != null) { _logger?.Information("[AxialStyle] ✅ Got connection from OE ToolWindow"); TryUpdateCache(connInfo); return connInfo; }

            // 策略C: VsServiceProvider — ServiceCache.VsServiceProvider 可用
            connInfo = ValidateServerMatch(TryGetFromVsServiceProvider(), windowServer);
            if (connInfo != null) { _logger?.Information("[AxialStyle] ✅ Got connection from VsServiceProvider"); TryUpdateCache(connInfo); return connInfo; }

            // 策略D: ConnectionStore — 缓存连接探索
            connInfo = ValidateServerMatch(TryGetFromConnectionStore(), windowServer);
            if (connInfo != null) { _logger?.Information("[AxialStyle] ✅ Got connection from ConnectionStore"); TryUpdateCache(connInfo); return connInfo; }

            // 策略E: ScriptFactory 兜底
            connInfo = ValidateServerMatch(TryGetFromScriptFactory(), windowServer);
            if (connInfo != null) { _logger?.Information("[AxialStyle] ✅ Got connection from ScriptFactory"); TryUpdateCache(connInfo); return connInfo; }

            _logger?.Warning("[AxialStyle] ❌ All strategies failed");
            return null;
        }
        catch (Exception ex)
        {
            _logger?.Error(ex, "[AxialStyle] Unexpected exception in provider");
            return null;
        }
    }

    /// <summary>
    /// 记录窗口标题解析出的服务器名（实例字段 + 静态属性同步，供 ConnectionFacade 跨 provider 校验）。
    /// </summary>
    private void SetWindowCaptionServer(string server, string user)
    {
        _windowCaptionServer = server;
        _windowCaptionUser = user;
        LastWindowCaptionServer = server;
    }

    /// <summary>
    /// 服务器一致性校验：策略 B~E 获取的连接服务器必须与窗口标题解析的服务器一致。
    /// 不一致时拒绝使用（返回 null），避免：
    /// 1. 用户切换到新服务器（缓存无凭据）时，Object Explorer 仍停留在旧服务器节点
    /// 2. 策略B~E 返回旧服务器的连接 → 补全/缓存使用错误服务器
    /// 3. 上层 DetectServerSwitch 无法识别"获取失败" → 无任何提示
    /// </summary>
    private ConnectionInfo? ValidateServerMatch(ConnectionInfo? info, string? expectedServer)
    {
        if (info == null || string.IsNullOrEmpty(expectedServer))
            return info;

        if (string.Equals(info.ServerInstance, expectedServer, StringComparison.OrdinalIgnoreCase))
            return info;

        _logger?.Warning($"[AxialStyle] ⚠️ 服务器不匹配: 窗口标题='{expectedServer}', 策略获取='{info.ServerInstance}'，" +
                         $"拒绝使用该连接（Object Explorer 节点可能未同步切换到新服务器）");
        return null;
    }

    // ============================================================
    // 策略A: DTE 窗口标题解析 + 活动窗口深度探索
    // 标题格式：SQLQuery1.sql - 172.16.202.197.TGMES (yaoshaobo (72))
    // 提取 Server.Database + UserName + SPID
    // 同时尝试从 Document.Object COM 对象中获取已打开的 SqlConnection
    // ============================================================
    private ConnectionInfo? TryGetFromWindowCaption()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[AxialStyle][策略A] Window Caption + COM exploration...");

            var dteType = Type.GetType("EnvDTE.DTE, EnvDTE");
            var dte = dteType != null ? _serviceProvider.GetService(dteType) : null;
            if (dte == null) return null;

            dynamic dteDynamic = dte;
            dynamic? activeDoc = null;
            try { activeDoc = dteDynamic.ActiveDocument; } catch { return null; }

            if (activeDoc == null) return null;

            // 获取活动窗口的标题
            string caption = "";
            try { caption = dteDynamic.ActiveWindow?.Caption ?? ""; } catch { }

            if (string.IsNullOrWhiteSpace(caption))
            {
                try { caption = activeDoc.Name ?? ""; } catch { }
            }

            _logger?.Debug($"[AxialStyle][策略A] Window caption: {caption}");

            // ============================================================
            // 步骤1: 正则解析标题 - 打印所有 Groups 用于诊断
            // ============================================================
            // 格式：SQLQuery1.sql - 172.16.202.197.TGMES (yaoshaobo (72))
            // Group 1 = server, Group 2 = database, Group 3 = username, Group 4 = spid
            var match = Regex.Match(caption,
                @"\s-\s+([\w\.\\\-\(\)]+(?:,\d+)?)\.(\w+)\s*\((\S+)\s*\((\d+)\)\)",
                RegexOptions.IgnoreCase);

            // 打印所有 Groups 用于诊断
            if (match.Success)
            {
                _logger?.Information("[AxialStyle][策略A] === Regex Match Groups ===");
                for (int i = 0; i < match.Groups.Count; i++)
                {
                    _logger?.Information($"[AxialStyle][策略A]   Groups[{i}] = '{match.Groups[i].Value}' (Success={match.Groups[i].Success})");
                }
            }

            if (!match.Success)
            {
                // 尝试不带 SPID 的匹配：SQLQuery1.sql - server.db (username)
                match = Regex.Match(caption,
                    @"\s-\s+([\w\.\\\-\(\)]+(?:,\d+)?)\.(\w+)\s*\((\S+)\)",
                    RegexOptions.IgnoreCase);

                if (match.Success)
                {
                    _logger?.Information("[AxialStyle][策略A] === Regex Match Groups (no SPID) ===");
                    for (int i = 0; i < match.Groups.Count; i++)
                    {
                        _logger?.Information($"[AxialStyle][策略A]   Groups[{i}] = '{match.Groups[i].Value}'");
                    }

                    string server = match.Groups[1].Value;
                    string db = match.Groups[2].Value;
                    string user = match.Groups[3].Value;

                    _logger?.Information($"[AxialStyle][策略A] Parsed: Server={server}, DB={db}, User={user}");

                    // 记录窗口标题服务器（供一致性校验）
                    SetWindowCaptionServer(server, user);

                    return BuildConnectionInfo(server, db, user, "", null, null);
                }

                // 最简匹配：仅 server.db
                match = Regex.Match(caption, @"\s-\s+([\w\.\\\-\(\)]+(?:,\d+)?)\.(\w+)\s*\(");
                if (!match.Success)
                {
                    match = Regex.Match(caption, @"\s-\s+([\w\.\\\-\(\)]+(?:,\d+)?)\s*\(");
                    if (match.Success)
                    {
                        string server = match.Groups[1].Value;
                        _logger?.Information($"[AxialStyle][策略A] Parsed server only: {server}");

                        // 记录窗口标题服务器（供一致性校验）
                        SetWindowCaptionServer(server, "");

                        string? dbName = TryGetDatabaseFromActiveDoc(activeDoc);
                        if (dbName != null)
                        {
                            _logger?.Information($"[AxialStyle][策略A] Got database from DTE COM: {dbName}");
                        }
                        else
                        {
                            _logger?.Warning($"[AxialStyle][策略A] Cannot resolve database name for server {server}, will show as unidentified");
                        }
                        return BuildConnectionInfo(server, dbName ?? "", "", "", null, null);
                    }
                    return null;
                }
            }

            string serverName = match.Groups[1].Value;
            string databaseName = match.Groups[2].Value;
            string userName = match.Groups.Count > 3 ? match.Groups[3].Value : "";
            string spid = match.Groups.Count > 4 ? match.Groups[4].Value : "";

            if (string.IsNullOrWhiteSpace(serverName))
                return null;

            // 记录窗口标题服务器（供后续策略一致性校验）
            SetWindowCaptionServer(serverName, userName);

            _logger?.Information($"[AxialStyle][策略A] Parsed: Server={serverName}, DB={databaseName}, User={userName}, SPID={spid}");

            // ============================================================
            // 步骤2: 判断认证类型
            // ============================================================
            bool isWindowsAuth = false;
            bool isSqlAuth = false;

            if (!string.IsNullOrWhiteSpace(userName))
            {
                // 如果包含反斜杠或域名格式 → Windows Auth
                if (userName.Contains("\\") || userName.Contains("@"))
                {
                    isWindowsAuth = true;
                    _logger?.Debug($"[AxialStyle][策略A] Detected Windows Auth (domain\\user or UPN): {userName}");
                }
                else
                {
                    // 纯用户名（如 yaoshaobo）→ SQL Server Auth
                    isSqlAuth = true;
                    _logger?.Debug($"[AxialStyle][策略A] Detected SQL Server Auth: {userName}");
                }
            }

            // ============================================================
            // 步骤3: 尝试从 Document.Object COM 获取已打开的 SqlConnection
            // ============================================================
            var sqlConnFromDoc = TryExtractSqlConnectionFromActiveDoc(activeDoc);
            if (sqlConnFromDoc != null)
            {
                _logger?.Information($"[AxialStyle][策略A] ✅ Got live SqlConnection from Document.Object!");
                return sqlConnFromDoc;
            }

            // ============================================================
            // 步骤4: 尝试使用缓存凭据
            // ============================================================
            if (isSqlAuth)
            {
                // 4a. 先查缓存：窗口标题有 Server/User 信息，尝试用缓存的密码连接
                var cached = _connectionCache.Get(serverName);
                if (cached != null)
                {
                    _logger?.Information($"[AxialStyle][策略A] Found cached credentials for {serverName}, testing...");
                    if (_connectionCache.TestConnection(cached, databaseName))
                    {
                        _logger?.Information($"[AxialStyle][策略A] ✅ Cached credentials still valid for {serverName}");
                        return BuildConnectionInfo(serverName, databaseName,
                            cached.UserName, cached.Password,
                            cached.EncryptConnection?.ToString()?.ToLower(),
                            cached.TrustServerCertificate?.ToString()?.ToLower());
                    }
                    _logger?.Warning($"[AxialStyle][策略A] Cached credentials expired for {serverName}, removing...");
                    _connectionCache.Remove(serverName);
                }

                // 4b. 缓存无数据或已失效，回退到后续策略
                _logger?.Information("[AxialStyle][策略A] No valid cached credentials, falling back to strategies B~E");
                return null;
            }

            // Windows Auth 或无信息：Integrated Security（无需密码）
            return BuildConnectionInfo(serverName, databaseName, "", "", null, null);
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[AxialStyle][策略A] Failed: {ex.Message}");
            return null;
        }
    }

    // ============================================================
    // 辅助：尝试从 Document.Object COM 中提取已打开的 SqlConnection
    // ============================================================
    private ConnectionInfo? TryExtractSqlConnectionFromActiveDoc(dynamic activeDoc)
    {
        try
        {
            _logger?.Debug("[AxialStyle][策略A-COM] Attempting to extract SqlConnection from Document.Object...");

            // 步骤1: 获取 Document.Object COM 包装对象
            dynamic? docObj = null;
            try { docObj = activeDoc.Object; } catch { }

            if (docObj == null)
            {
                _logger?.Debug("[AxialStyle][策略A-COM] Document.Object is null");
                return null;
            }

            var objType = docObj.GetType();
            _logger?.Debug($"[AxialStyle][策略A-COM] Document.Object CLR type: {objType.FullName}");

            // 步骤2: 如果 CLR 类型是 __ComObject，用反射枚举其属性
            // COM 对象的属性不一定能通过 dynamic 访问，但可以通过 Type.InvokeMember 调用 IDispatch
            var bindingFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.NonPublic;

            // 尝试所有可能的连接相关属性名
            string[] connPropNames = {
                "Connection", "CurrentConnection", "ServerConnection",
                "SqlConnection", "DbConnection", "ActiveConnection",
                "ConnectionInfo", "SqlConnectionInfo", "Server"
            };

            foreach (var propName in connPropNames)
            {
                try
                {
                    var prop = objType.GetProperty(propName, bindingFlags);
                    if (prop == null) continue;

                    var value = prop.GetValue(docObj);
                    if (value == null) continue;

                    _logger?.Debug($"[AxialStyle][策略A-COM] Found property: {propName} = {value.GetType().FullName}");
                    var connInfo = TryExtractConnectionFromPropertyValue(value);
                    if (connInfo != null) return connInfo;
                }
                catch (Exception ex)
                {
                    _logger?.Debug($"[AxialStyle][策略A-COM]   {propName}: access failed ({ex.Message})");
                }
            }

            // 步骤3: 尝试 COM IDispatch 方式调用方法
            // 使用 Type.InvokeMember 调用 COM 对象的方法
            try
            {
                var result = objType.InvokeMember("GetConnectionInfo",
                    BindingFlags.InvokeMethod | BindingFlags.Instance | BindingFlags.Public,
                    null, docObj, null);
                if (result != null)
                {
                    _logger?.Debug($"[AxialStyle][策略A-COM] GetConnectionInfo() returned: {result.GetType().FullName}");
                    var connInfo = TryExtractConnectionFromPropertyValue(result);
                    if (connInfo != null) return connInfo;
                }
            }
            catch { }

            // 步骤4: 尝试枚举所有方法，找返回 SqlConnection 的
            try
            {
                var methods = objType.GetMethods(BindingFlags.Public | BindingFlags.Instance);
                foreach (var method in methods)
                {
                    bool isSqlConnMethod = method.ReturnType.FullName?.Contains("SqlConnection") == true
                                        && method.GetParameters().Length == 0;
                    if (!isSqlConnMethod) continue;

                    try
                    {
                        var result = method.Invoke(docObj, null);
                        if (result != null)
                        {
                            _logger?.Debug($"[AxialStyle][策略A-COM] Method {method.Name}() returned SqlConnection");
                            var connInfo = TryExtractConnectionFromPropertyValue(result);
                            if (connInfo != null) return connInfo;
                        }
                    }
                    catch { }
                }
            }
            catch { }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[AxialStyle][策略A-COM] Failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 轻量级尝试从 DTE ActiveDocument.Object 中提取数据库名。
    /// 仅在正则解析到服务器名但未解析到数据库名时调用，不涉及密码/凭据逻辑。
    /// </summary>
    private string? TryGetDatabaseFromActiveDoc(dynamic activeDoc)
    {
        try
        {
            _logger?.Debug("[AxialStyle][策略A-DB] Attempting to extract database name from Document.Object...");

            dynamic? docObj = null;
            try { docObj = activeDoc.Object; } catch { }
            if (docObj == null)
            {
                _logger?.Debug("[AxialStyle][策略A-DB] Document.Object is null");
                return null;
            }

            var objType = docObj.GetType();

            // 尝试1: 直接获取 Database 属性
            try
            {
                var dbProp = objType.GetProperty("Database", BindingFlags.Public | BindingFlags.Instance);
                if (dbProp != null)
                {
                    var dbValue = dbProp.GetValue(docObj) as string;
                    if (!string.IsNullOrWhiteSpace(dbValue))
                    {
                        _logger?.Debug($"[AxialStyle][策略A-DB] Found Database property: {dbValue}");
                        return dbValue;
                    }
                }
            }
            catch { }

            // 尝试2: 通过 Connection 属性获取 Connection.Database
            try
            {
                var connProp = objType.GetProperty("Connection", BindingFlags.Public | BindingFlags.Instance);
                if (connProp != null)
                {
                    var connValue = connProp.GetValue(docObj);
                    if (connValue != null)
                    {
                        var connType = connValue.GetType();
                        var dbProp = connType.GetProperty("Database", BindingFlags.Public | BindingFlags.Instance);
                        if (dbProp != null)
                        {
                            var dbValue = dbProp.GetValue(connValue) as string;
                            if (!string.IsNullOrWhiteSpace(dbValue))
                            {
                                _logger?.Debug($"[AxialStyle][策略A-DB] Found Connection.Database: {dbValue}");
                                return dbValue;
                            }
                        }
                    }
                }
            }
            catch { }

            // 尝试3: 通过 ActiveConnection 属性获取
            try
            {
                var acProp = objType.GetProperty("ActiveConnection", BindingFlags.Public | BindingFlags.Instance);
                if (acProp != null)
                {
                    var acValue = acProp.GetValue(docObj);
                    if (acValue != null)
                    {
                        var acType = acValue.GetType();
                        var dbProp = acType.GetProperty("Database", BindingFlags.Public | BindingFlags.Instance);
                        if (dbProp != null)
                        {
                            var dbValue = dbProp.GetValue(acValue) as string;
                            if (!string.IsNullOrWhiteSpace(dbValue))
                            {
                                _logger?.Debug($"[AxialStyle][策略A-DB] Found ActiveConnection.Database: {dbValue}");
                                return dbValue;
                            }
                        }
                    }
                }
            }
            catch { }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[AxialStyle][策略A-DB] Failed: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 从对象属性值中提取 SqlConnection 信息
    /// </summary>
    private ConnectionInfo? TryExtractConnectionFromPropertyValue(object value)
    {
        // 直接是 SqlConnection 类型
        if (value is System.Data.SqlClient.SqlConnection sysConn)
        {
            return ExtractFromSqlClientConnection(sysConn);
        }

        var type = value.GetType();

        // 如果是 SqlConnection 子类型或包装类型
        if (type.FullName?.Contains("SqlConnection") == true)
        {
            try
            {
                var ds = type.GetProperty("DataSource")?.GetValue(value) as string;
                var db = type.GetProperty("Database")?.GetValue(value) as string;
                var cs = type.GetProperty("ConnectionString")?.GetValue(value) as string;
                if (!string.IsNullOrWhiteSpace(ds))
                    return new ConnectionInfo
                    {
                        SourceProvider = ProviderName + "[COM]",
                        ServerInstance = ds,
                        DatabaseName = db,
                        ConnectionString = cs,
                        AuthenticationType = AuthenticationType.Unknown
                    };
            }
            catch { }
        }

        // 嵌套探索：找 .SqlConnection 或 .Connection 属性
        try
        {
            var sqlConnProp = type.GetProperty("SqlConnection",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            if (sqlConnProp != null)
            {
                var innerConn = sqlConnProp.GetValue(value);
                if (innerConn is System.Data.SqlClient.SqlConnection innerSysConn)
                {
                    return ExtractFromSqlClientConnection(innerSysConn);
                }
            }
        }
        catch { }

        return null;
    }

    private ConnectionInfo? ExtractFromSqlClientConnection(System.Data.SqlClient.SqlConnection conn)
    {
        try
        {
            string server = conn.DataSource ?? "";
            string db = conn.Database ?? "";
            string connStr = conn.ConnectionString ?? "";

            _logger?.Information($"[AxialStyle][策略A-COM] Got SqlConnection: Server={server}, DB={db}, State={conn.State}");

            // 数据库名为空时不返回 ConnectionString（避免使用无数据库名的连接串）
            var connString = string.IsNullOrEmpty(db) ? null : connStr;

            return new ConnectionInfo
            {
                SourceProvider = ProviderName + "[COM-SqlConnection]",
                ServerInstance = server,
                DatabaseName = db,
                ConnectionString = connString,
                AuthenticationType = AuthenticationType.Unknown
            };
        }
        catch
        {
            return null;
        }
    }

    // ============================================================
    // 策略B: ObjectExplorerToolWindow 深度探索
    // 日志证明可用：ObjectExplorerToolWindow 类型可通过 DTE 获取
    // 通过反射探索其内部，获取 SelectedNode → Connection
    // ============================================================
    private ConnectionInfo? TryGetFromOEToolWindow()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[AxialStyle][策略B] ObjectExplorerToolWindow...");

            var dteType = Type.GetType("EnvDTE.DTE, EnvDTE");
            var dte = dteType != null ? _serviceProvider.GetService(dteType) : null;
            if (dte == null) return null;

            dynamic dteDynamic = dte;
            dynamic? windows = null;
            try { windows = dteDynamic.Windows; } catch { return null; }
            if (windows == null) return null;

            // 遍历所有窗口，找 Object Explorer
            foreach (dynamic window in windows)
            {
                try
                {
                    string? wCaption = null;
                    try { wCaption = window.Caption ?? ""; } catch { }

                    if (!string.IsNullOrWhiteSpace(wCaption) &&
                        (wCaption.Contains("Object Explorer") || wCaption.Contains("对象资源管理器")))
                    {
                        _logger?.Debug($"[AxialStyle][策略B] Found Object Explorer: {wCaption}");

                        // 获取 Window.Object
                        dynamic? obj = null;
                        try { obj = window.Object; } catch { }

                        if (obj != null)
                        {
                            var objType = obj.GetType();
                            _logger?.Debug($"[AxialStyle][策略B] OE Object type: {objType.FullName}");

                            // 深度探索OE工具窗口对象的属性和方法
                            return ExtractFromOEObject(obj, objType);
                        }
                    }
                }
                catch { }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[AxialStyle][策略B] Failed: {ex.Message}");
            return null;
        }
    }

    private ConnectionInfo? ExtractFromOEObject(object oeObj, Type oeType)
    {
        // 探索所有属性，找 Connection/Server/SelectedNode 相关属性
        var props = oeType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var prop in props)
        {
            try
            {
                bool isConnRelated = prop.Name.Contains("Connection") ||
                                     prop.Name.Contains("Server") ||
                                     prop.Name.Contains("Selected") ||
                                     prop.Name.Contains("Active") ||
                                     prop.Name.Contains("Current");

                if (!isConnRelated) continue;

                var val = prop.GetValue(oeObj);
                if (val == null) continue;

                _logger?.Debug($"[AxialStyle][策略B]   {prop.Name}({prop.PropertyType.Name}) = {val.GetType().FullName}");

                // 如果是SqlConnection或ServerConnection类型
                var connInfo = ExtractFromObject(val, $"[策略B-{prop.Name}]");
                if (connInfo != null) return connInfo;
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[AxialStyle][策略B]   {prop.Name}: [访问失败: {ex.Message}]");
            }
        }

        // 探索方法（GetSelectedNodes, GetCurrentConnection等）
        var methods = oeType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        foreach (var method in methods)
        {
            try
            {
                bool isRelevant = method.Name.Contains("Selected") ||
                                  method.Name.Contains("Connection") ||
                                  method.Name.Contains("Active");
                if (!isRelevant || method.GetParameters().Length > 2) continue;

                _logger?.Debug($"[AxialStyle][策略B] Trying method: {method.Name}");

                // 尝试无参调用
                if (method.GetParameters().Length == 0)
                {
                    var result = method.Invoke(oeObj, null);
                    if (result != null)
                    {
                        _logger?.Debug($"[AxialStyle][策略B]   {method.Name}() returned: {result.GetType().FullName}");
                        var connInfo = ExtractFromObject(result, $"[策略B-{method.Name}]");
                        if (connInfo != null) return connInfo;
                    }
                }
                // 尝试 out int + out Array 模式 (GetSelectedNodes)
                else if (method.GetParameters().Length == 2 &&
                         method.GetParameters()[0].ParameterType == typeof(int).MakeByRefType())
                {
                    var parameters = new object[] { 0, null! };
                    method.Invoke(oeObj, parameters);
                    var nodes = parameters[1] as Array;
                    if (nodes != null && nodes.Length > 0 && nodes.GetValue(0) != null)
                    {
                        var node = nodes.GetValue(0);
                        var connInfo = ExtractFromObject(node, $"[策略B-{method.Name}-Node]");
                        if (connInfo != null) return connInfo;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[AxialStyle][策略B]   {method.Name}: [调用失败: {ex.Message}]");
            }
        }

        return null;
    }

    // ============================================================
    // 策略C: VsServiceProvider
    // 日志证明：ServiceCache.VsServiceProvider 可用（非 null）
    // 尝试通过该 VS 级服务提供者获取 ObjectExplorerService
    // ============================================================
    private ConnectionInfo? TryGetFromVsServiceProvider()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[AxialStyle][策略C] VsServiceProvider...");

            var serviceCacheType = GetServiceCacheType();
            if (serviceCacheType == null) return null;

            // 获取 VsServiceProvider（日志证明可用）
            var vsSpProp = serviceCacheType.GetProperty("VsServiceProvider",
                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            if (vsSpProp == null)
            {
                _logger?.Debug("[AxialStyle][策略C] VsServiceProvider property not found");
                return null;
            }

            var vsServiceProvider = vsSpProp.GetValue(null) as IServiceProvider;
            if (vsServiceProvider == null)
            {
                _logger?.Debug("[AxialStyle][策略C] VsServiceProvider is null");
                return null;
            }

            _logger?.Debug($"[AxialStyle][策略C] VsServiceProvider type: {vsServiceProvider.GetType().FullName}");

            // 尝试多种 IObjectExplorerService 类型名称
            string[] oeTypeNames =
            {
                "Microsoft.SqlServer.Management.UI.VSIntegration.ObjectExplorer.IObjectExplorerService",
                "Microsoft.SqlServer.Management.SqlStudio.Explorer.IObjectExplorerService",
                "Microsoft.SqlServer.Management.SqlStudio.Explorer.ObjectExplorerService"
            };

            foreach (var typeName in oeTypeNames)
            {
                var oeType = SmartAssemblyLoader.GetType(typeName, "ObjectExplorer")
                    ?? SmartAssemblyLoader.GetType(typeName, "VSIntegration");

                if (oeType == null)
                {
                    foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                    {
                        try
                        {
                            oeType = asm.GetType(typeName);
                            if (oeType != null) break;
                        }
                        catch { }
                    }
                }

                if (oeType == null) continue;

                _logger?.Debug($"[AxialStyle][策略C] Found type: {oeType.FullName} in {oeType.Assembly.GetName().Name}");

                var oeService = vsServiceProvider.GetService(oeType);
                if (oeService == null)
                {
                    _logger?.Debug($"[AxialStyle][策略C] Service not available for {oeType.Name}");
                    continue;
                }

                _logger?.Debug($"[AxialStyle][策略C] Got service: {oeService.GetType().FullName}");

                var connInfo = ExtractFromOEObject(oeService, oeService.GetType());
                if (connInfo != null) return connInfo;
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[AxialStyle][策略C] Failed: {ex.Message}");
            return null;
        }
    }

    // ============================================================
    // 策略D: ConnectionStore — 探索缓存连接
    // 日志证明：Microsoft.SqlServer.Management.ConnectionStore 存在
    // ============================================================
    private ConnectionInfo? TryGetFromConnectionStore()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[AxialStyle][策略D] ConnectionStore...");

            // ConnectionStore 在 Microsoft.SqlServer.Management.SDK.SqlStudio 中
            var storeType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.Management.ConnectionStore",
                "SqlStudioExplorer");

            if (storeType == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        storeType = asm.GetType("Microsoft.SqlServer.Management.ConnectionStore");
                        if (storeType != null) break;
                    }
                    catch { }
                }
            }

            if (storeType == null)
            {
                _logger?.Debug("[AxialStyle][策略D] ConnectionStore type not found");
                return null;
            }

            _logger?.Debug($"[AxialStyle][策略D] Found ConnectionStore in {storeType.Assembly.GetName().Name}");

            // 找静态 Instance/Default/Current 属性
            var staticProps = storeType.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            foreach (var sp in staticProps)
            {
                if (sp.Name is "Instance" or "Default" or "Current")
                {
                    try
                    {
                        var instance = sp.GetValue(null);
                        if (instance != null)
                        {
                            _logger?.Debug($"[AxialStyle][策略D] Got ConnectionStore instance: {instance.GetType().FullName}");
                            var connInfo = ExtractFromObject(instance, "[策略D]");
                            if (connInfo != null) return connInfo;
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.Debug($"[AxialStyle][策略D] {sp.Name}: {ex.Message}");
                    }
                }
            }

            return null;
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[AxialStyle][策略D] Failed: {ex.Message}");
            return null;
        }
    }

    // ============================================================
    // 策略E: ScriptFactory 兜底
    // 虽然日志证明内部抛 NullReferenceException，但在某些 SSMS 22 版本可能可用
    // ============================================================
    private ConnectionInfo? TryGetFromScriptFactory()
    {
        ThreadHelper.ThrowIfNotOnUIThread();

        try
        {
            _logger?.Debug("[AxialStyle][策略E] ScriptFactory (last resort)...");

            var serviceCacheType = GetServiceCacheType();
            if (serviceCacheType == null) return null;

            dynamic? scriptFactory = null;
            try
            {
                var sfProp = serviceCacheType.GetProperty("ScriptFactory",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
                if (sfProp != null)
                    scriptFactory = sfProp.GetValue(null);
            }
            catch (Exception ex)
            {
                _logger?.Debug($"[AxialStyle][策略E] ScriptFactory getter failed: {ex.Message}");
                return null;
            }

            if (scriptFactory == null) return null;

            dynamic? connInfo = null;
            try { connInfo = scriptFactory.CurrentlyActiveWndConnectionInfo; } catch { return null; }
            if (connInfo == null) return null;

            dynamic? uiConn = null;
            try { uiConn = connInfo.UIConnectionInfo; } catch { return null; }
            if (uiConn == null) return null;

            string serverName = "";
            try { serverName = uiConn.ServerName ?? ""; } catch { }
            string userName = "";
            try { userName = uiConn.UserName ?? ""; } catch { }
            string password = "";
            try { password = uiConn.Password ?? ""; } catch { }
            string databaseName = "";
            try
            {
                dynamic? advOpts = uiConn.AdvancedOptions;
                if (advOpts != null)
                {
                    string? dbValue = advOpts.Get("DATABASE");
                    if (!string.IsNullOrWhiteSpace(dbValue)) databaseName = dbValue;
                }
            }
            catch { }

            if (string.IsNullOrWhiteSpace(serverName)) return null;
            if (string.IsNullOrWhiteSpace(databaseName)) return null;

            string? encryptConn = null, trustCert = null;
            try { dynamic? adv = uiConn.AdvancedOptions; if (adv != null) encryptConn = adv.Get("ENCRYPT_CONNECTION"); } catch { }
            try { dynamic? adv = uiConn.AdvancedOptions; if (adv != null) trustCert = adv.Get("TRUST_SERVER_CERTIFICATE"); } catch { }

            _logger?.Information($"[AxialStyle][策略E] ScriptFactory: Server={serverName}, DB={databaseName}");
            return BuildConnectionInfo(serverName, databaseName, userName, password, encryptConn, trustCert);
        }
        catch (Exception ex)
        {
            _logger?.Debug($"[AxialStyle][策略E] Failed: {ex.Message}");
            return null;
        }
    }

    // ============================================================
    // 通用提取方法
    // ============================================================

    private ConnectionInfo? ExtractFromObject(object obj, string tag)
    {
        if (obj == null) return null;

        try
        {
            var type = obj.GetType();

            // SqlConnection
            if (type.FullName?.Contains("SqlConnection") == true)
            {
                var ds = type.GetProperty("DataSource")?.GetValue(obj) as string;
                var db = type.GetProperty("Database")?.GetValue(obj) as string;
                var cs = type.GetProperty("ConnectionString")?.GetValue(obj) as string;
                if (!string.IsNullOrWhiteSpace(ds))
                    return new ConnectionInfo
                    {
                        SourceProvider = ProviderName + tag,
                        ServerInstance = ds,
                        DatabaseName = db,
                        ConnectionString = cs,
                        AuthenticationType = AuthenticationType.Unknown
                    };
            }

            // UIConnectionInfo (SSMS internal: Microsoft.SqlServer.Management.Smo.RegSvrEnum.UIConnectionInfo)
            // 关键修复：DatabaseName 存储在 AdvancedOptions["DATABASE"] 字典中，而非直接属性
            // 不加此分支时，下方 ServerConnection 分支能匹配到 ServerName 属性，
            // 但 DatabaseName/Database/InitialCatalog 均为 null → return null → 连接失败
            if (type.FullName?.Contains("UIConnectionInfo") == true)
            {
                string uiServer = type.GetProperty("ServerName")?.GetValue(obj) as string ?? "";
                string uiUser = type.GetProperty("UserName")?.GetValue(obj) as string ?? "";
                string uiPass = type.GetProperty("Password")?.GetValue(obj) as string ?? "";
                string uiDatabase = "";
                string? uiEncrypt = null;
                string? uiTrustCert = null;

                try
                {
                    dynamic? advOpts = type.GetProperty("AdvancedOptions")?.GetValue(obj);
                    if (advOpts != null)
                    {
                        uiDatabase = advOpts.Get("DATABASE") ?? "";
                        uiEncrypt = advOpts.Get("ENCRYPT_CONNECTION");
                        uiTrustCert = advOpts.Get("TRUST_SERVER_CERTIFICATE");
                    }
                }
                catch { }

                _logger?.Debug($"[AxialStyle][ExtractFromObject] UIConnectionInfo: Server={uiServer}, DB={uiDatabase}, User={uiUser}, HasPass={!string.IsNullOrEmpty(uiPass)}");

                if (string.IsNullOrWhiteSpace(uiServer) || string.IsNullOrWhiteSpace(uiDatabase))
                {
                    _logger?.Debug($"[AxialStyle][ExtractFromObject] UIConnectionInfo: server or database is empty, skipping");
                    return null;
                }

                return BuildConnectionInfo(uiServer, uiDatabase, uiUser, uiPass, uiEncrypt, uiTrustCert);
            }

            // ServerConnection / IServerConnection
            var serverProp = type.GetProperty("ServerName") ??
                             type.GetProperty("ServerInstance") ??
                             type.GetProperty("DataSource");
            if (serverProp != null)
            {
                var server = serverProp.GetValue(obj) as string;
                if (!string.IsNullOrWhiteSpace(server))
                {
                    var dbProp = type.GetProperty("DatabaseName") ??
                                 type.GetProperty("Database") ??
                                 type.GetProperty("InitialCatalog");
                    var userProp = type.GetProperty("UserName") ?? type.GetProperty("Login");
                    var passProp = type.GetProperty("Password");
                    var csProp = type.GetProperty("ConnectionString");

                    string dbName = dbProp?.GetValue(obj) as string ?? "";
                    string user = userProp?.GetValue(obj) as string ?? "";
                    string pass = passProp?.GetValue(obj) as string ?? "";
                    string? connStr = csProp?.GetValue(obj) as string;

                    if (string.IsNullOrWhiteSpace(dbName)) return null;

                    return BuildConnectionInfo(server, dbName, user, pass, null, null);
                }
            }

            // 递归探索嵌套对象
            var nestedProps = type.GetProperties(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            foreach (var prop in nestedProps)
            {
                if (!prop.Name.Contains("Connection") && !prop.Name.Contains("Server") &&
                    !prop.Name.Contains("Database") && !prop.Name.Contains("Sql"))
                    continue;
                try
                {
                    var val = prop.GetValue(obj);
                    if (val != null)
                    {
                        var nested = ExtractFromObject(val, $"{tag}.{prop.Name}");
                        if (nested != null) return nested;
                    }
                }
                catch { }
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    // ============================================================
    // 辅助方法
    // ============================================================

    private static Type? GetServiceCacheType()
    {
        if (_serviceCacheType != null) return _serviceCacheType;

        lock (_typeLock)
        {
            if (_serviceCacheType != null) return _serviceCacheType;

            _serviceCacheType = SmartAssemblyLoader.GetType(
                "Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache",
                "VSIntegration");

            if (_serviceCacheType == null)
            {
                foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
                {
                    try
                    {
                        var t = asm.GetType("Microsoft.SqlServer.Management.UI.VSIntegration.ServiceCache");
                        if (t != null) { _serviceCacheType = t; break; }
                    }
                    catch { }
                }
            }

            return _serviceCacheType;
        }
    }

    /// <summary>
    /// 从成功获取的 ConnectionInfo 中提取凭据并更新缓存。
    /// 当策略 B~E 成功获取连接信息（含密码）时调用。
    /// </summary>
    private void TryUpdateCache(ConnectionInfo connInfo)
    {
        var cached = ConnectionCache.ExtractFromConnectionInfo(connInfo);
        if (cached != null && !string.IsNullOrWhiteSpace(cached.ServerName) && !string.IsNullOrWhiteSpace(cached.Password))
        {
            _connectionCache.Set(cached.ServerName, cached);
        }
    }

    private ConnectionInfo BuildConnectionInfo(
        string serverName, string databaseName,
        string userName, string password,
        string? encryptConnection, string? trustServerCert)
    {
        var builder = new SqlConnectionStringBuilder
        {
            DataSource = serverName,
            InitialCatalog = databaseName,
            ApplicationName = "SSMSBoostPlus",
            ConnectTimeout = 30
        };

        // 认证模式
        if (!string.IsNullOrWhiteSpace(password))
        {
            builder.IntegratedSecurity = false;
            builder.UserID = userName;
            builder.Password = password;
        }
        else if (!string.IsNullOrWhiteSpace(userName))
        {
            // SQL Auth 但没有密码：必须设 IntegratedSecurity=false，否则 SqlClient 会尝试 Windows Auth
            builder.IntegratedSecurity = false;
            builder.UserID = userName;
            // 注意：缺少密码时连接可能失败，但错误会明确提示 "Login failed for user"
            // 而不是 SSL 证书错误，便于日志诊断
        }
        else
        {
            builder.IntegratedSecurity = true;
        }

        // 加密设置：SSMS 22 默认启用加密，必须配置 TrustServerCertificate
        // 从原始 UIConnectionInfo 读取的标志优先，否则使用安全默认值
        if (encryptConnection != null)
        {
            builder.Encrypt = encryptConnection.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // SSMS 22 默认启用 Strict Encryption，扩展也必须启用
            builder.Encrypt = true;
        }

        if (trustServerCert != null)
        {
            builder.TrustServerCertificate = trustServerCert.Equals("true", StringComparison.OrdinalIgnoreCase);
        }
        else
        {
            // 关键修复：当从窗口标题解析连接信息时，没有加密设置，
            // 必须启用 TrustServerCertificate 否则 SSL 证书验证会失败
            builder.TrustServerCertificate = true;
        }

        return new ConnectionInfo
        {
            SourceProvider = ProviderName,
            ServerInstance = serverName,
            DatabaseName = databaseName,
            UserName = userName,
            AuthenticationType = !string.IsNullOrWhiteSpace(userName) && string.IsNullOrWhiteSpace(password)
                ? AuthenticationType.SqlServer
                : (string.IsNullOrWhiteSpace(userName)
                    ? AuthenticationType.Windows
                    : AuthenticationType.SqlServer),
            ConnectionString = builder.ToString()
        };
    }
}
