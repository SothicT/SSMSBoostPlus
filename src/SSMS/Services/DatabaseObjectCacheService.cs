using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using Microsoft.Data.SqlClient;
using Microsoft.VisualStudio.Shell;
using Newtonsoft.Json;
using SSMSBoostPlus.Interfaces;
using SSMSBoostPlus.Services;
using SSMSBoostPlus.SSMS.Services.Connection;
using SSMSBoostPlus.SSMS.UI.Dialogs;

namespace SSMSBoostPlus.SSMS.Services
{
    /// <summary>
    /// 数据库对象缓存服务。
    /// 
    /// 功能：
    /// 1. SSMS 启动后，自动检测当前连接并异步拉取数据库对象（表、视图、SP、函数）缓存到本地
    /// 2. 支持多数据库：缓存服务器上所有用户库的对象，key = "Server.Database"
    /// 3. 提供前缀搜索接口供智能补全弹窗使用（支持跨库查询）
    /// 4. 支持手动刷新缓存（覆盖旧数据）
    /// 
    /// 缓存结构：
    ///   Key = "ServerInstance.DatabaseName"
    ///   Value = DatabaseObjectCache (含对象列表、前缀索引、最后更新时间)
    ///   
    /// 持久化文件：{部署目录}\db_object_cache.json
    /// </summary>
    public class DatabaseObjectCacheService : ICompletionCacheProvider
    {
        private readonly ILoggerService _logger;
        private readonly ConnectionServiceFacade _connectionService;
        private readonly string _cacheFilePath;

        // 内存缓存：key = "server.database" → DatabaseObjectCache
        private readonly ConcurrentDictionary<string, DatabaseObjectCache> _cache = new();

        // 当前连接上下文对应的对象列表（供补全快速查询）
        // 使用 volatile 确保多线程读取的一致性
        private volatile List<DatabaseObjectInfo>? _currentObjects;
        private string? _currentCacheKey;
        private string? _currentServer;
        private string? _currentDatabase;

        // 当前服务器上的所有数据库名称（轻量，不包含对象）
        private List<string>? _allDatabaseNames;
        private int _allDatabasesFetched;

        // 防止重复自动加载
        private int _isAutoLoading = 0;

        // 并发拉取控制（限制同时查询数据库的数量）
        private static readonly SemaphoreSlim _fetchSemaphore = new(3, 3);

        // 连接失败 Toast 节流：切换新连接后 30 秒内只提示一次，避免频繁弹窗打扰
        private DateTime _lastConnectionToastTime = DateTime.MinValue;
        private static readonly TimeSpan ConnectionToastThrottle = TimeSpan.FromSeconds(30);
        private static readonly object _toastLock = new();

        /// <summary>补全候选最大数量，避免大库返回过多候选导致 UI 卡顿（US-01）</summary>
        internal const int MaxCompletionCandidates = 250;

        /// <summary>输入防抖窗口（US-06）</summary>
        internal const int DebounceMs = 120;

        /// <summary>结果 LRU 缓存容量（US-07）</summary>
        internal const int LruCapacity = 100;

        // US-07: 结果 LRU 缓存 —— key = "prefix" 或 "dbName|prefix"，value = 候选列表
        private readonly object _lruLock = new();
        private readonly LinkedList<string> _lruKeys = new();
        private readonly Dictionary<string, List<CompletionCandidate>> _lruCache = new();
        private int _lruCacheVersion = 0; // 缓存切换时递增，使旧 LRU 条目失效

        // 状态栏通知（可选）
        public event Action<string>? StatusChanged;

        public DatabaseObjectCacheService(
            ILoggerService logger,
            ConnectionServiceFacade connectionService,
            string configDirectory)
        {
            _logger = logger ?? throw new ArgumentNullException(nameof(logger));
            _connectionService = connectionService ?? throw new ArgumentNullException(nameof(connectionService));
            _cacheFilePath = Path.Combine(configDirectory, "db_object_cache.json");
        }

        // ============================================================
        // 分文件持久化（按 DB 分文件存储）
        // ============================================================

        private static readonly TimeSpan CacheExpiration = TimeSpan.FromHours(24);

        /// <summary>
        /// 根据 Server 和 Database 构建缓存文件名路径。
        /// Server 中的非法文件名字符替换为下划线。
        /// </summary>
        private string GetCacheFilePath(string server, string database)
        {
            var baseDir = Path.GetDirectoryName(
                System.Reflection.Assembly.GetExecutingAssembly().Location)!;
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

        /// <summary>
        /// 从按 DB 分文件的缓存中读取指定 Database 的缓存。
        /// </summary>
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
                    _logger.Information($"[DbCache] 缓存已过期: {filePath}");
                    return null; // 过期，触发重建
                }

                var json = File.ReadAllText(filePath, System.Text.Encoding.UTF8);
                var cache = JsonConvert.DeserializeObject<DatabaseObjectCache>(json);
                if (cache != null)
                {
                    // ★ 版本检查：旧版缓存不含 Parameters，跳过
                    if (cache.Version < DatabaseObjectCache.CurrentCacheVersion)
                    {
                        _logger.Information($"[DbCache] 缓存版本过旧（v{cache.Version} < v{DatabaseObjectCache.CurrentCacheVersion}），将重建: {database}");
                        return null;
                    }
                    cache.RebuildPrefixIndex();
                }
                return cache;
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DbCache] 读取缓存文件失败: {filePath}，将重建缓存 - {ex.Message}");
                return null; // 读取失败，触发重建
            }
        }

        /// <summary>
        /// 将指定 Database 的缓存保存到按 DB 分文件中。
        /// </summary>
        private void SaveCacheToFile(DatabaseObjectCache cache)
        {
            try
            {
                var filePath = GetCacheFilePath(cache.Server, cache.Database);
                var json = JsonConvert.SerializeObject(cache, Formatting.Indented);
                File.WriteAllText(filePath, json, System.Text.Encoding.UTF8);
                _logger.Information($"[DbCache] 已保存分文件缓存: {cache.Database}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DbCache] 保存分文件缓存失败: {cache.Database} - {ex.Message}");
                // 文件写入失败不影响内存缓存使用
            }
        }

        // ============================================================
        // 持久化
        // ============================================================

        /// <summary>
        /// 从本地分文件加载缓存到内存。
        /// 扫描 cache/ 目录下所有 *_object_cache.json 文件，逐个加载。
        /// 同时重建每个缓存的前缀索引。
        /// </summary>
        public async Task LoadFromFileAsync()
        {
            try
            {
                var baseDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location)!;
                var cacheDir = Path.Combine(baseDir, "cache");

                int loaded = 0, skipped = 0;

                // ★ 优先从分文件加载
                if (Directory.Exists(cacheDir))
                {
                    var files = await Task.Run(() =>
                        Directory.GetFiles(cacheDir, "*_object_cache.json"));

                    foreach (var file in files)
                    {
                        try
                        {
                            var json = await Task.Run(() => File.ReadAllText(file));
                            var cache = JsonConvert.DeserializeObject<DatabaseObjectCache>(json);
                            if (cache == null) continue;

                            if (cache.Version < DatabaseObjectCache.CurrentCacheVersion)
                            {
                                skipped++;
                                continue;
                            }

                            cache.RebuildPrefixIndex();
                            var cacheKey = $"{cache.Server}.{cache.Database}";
                            _cache[cacheKey] = cache;
                            loaded++;
                        }
                        catch (Exception ex)
                        {
                            _logger.Warning($"[DbCache] 加载分文件失败: {file} - {ex.Message}");
                        }
                    }
                }

                // ★ 兼容：如果分文件没加载到，尝试旧版单文件
                if (loaded == 0 && File.Exists(_cacheFilePath))
                {
                    var json = await Task.Run(() => File.ReadAllText(_cacheFilePath));
                    var dict = JsonConvert.DeserializeObject<Dictionary<string, DatabaseObjectCache>>(json);
                    if (dict != null)
                    {
                        foreach (var kvp in dict)
                        {
                            if (kvp.Value.Version < DatabaseObjectCache.CurrentCacheVersion)
                            {
                                skipped++;
                                continue;
                            }
                            kvp.Value.RebuildPrefixIndex();
                            _cache[kvp.Key] = kvp.Value;
                            loaded++;
                        }
                        _logger.Information($"[DbCache] 已从旧版单文件加载 {loaded} 个数据库的缓存（跳过 {skipped} 个旧版本）");
                    }
                }
                else if (loaded > 0)
                {
                    _logger.Information($"[DbCache] 已从分文件加载 {loaded} 个数据库的缓存（跳过 {skipped} 个旧版本），前缀索引已重建");
                }
                else
                {
                    _logger.Information("[DbCache] 无缓存文件，将在连接后自动创建");
                }
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[DbCache] 加载缓存文件失败");
            }
        }

        /// <summary>
        /// 保存内存缓存到本地文件。
        /// </summary>
        public async Task SaveToFileAsync()
        {
            try
            {
                var dict = new Dictionary<string, DatabaseObjectCache>(_cache);
                var json = JsonConvert.SerializeObject(dict, Formatting.Indented);
                await Task.Run(() => File.WriteAllText(_cacheFilePath, json));
                _logger.Information($"[DbCache] 已保存 {_cache.Count} 个数据库的缓存到文件");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[DbCache] 保存缓存文件失败");
            }
        }

        // ============================================================
        // 数据库名称获取
        // ============================================================

        /// <summary>
        /// 获取服务器上所有用户数据库的名称。
        /// </summary>
        private async Task<List<string>> FetchAllDatabaseNamesAsync(string connectionString)
        {
            var dbNames = new List<string>();
            try
            {
                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(
                    "SELECT name FROM sys.databases WHERE state = 0 AND database_id > 4 ORDER BY name",
                    conn);
                cmd.CommandTimeout = 15;
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var name = reader.GetString(0);
                    dbNames.Add(name);
                }
                _logger.Information($"[DbCache] 已获取服务器上 {dbNames.Count} 个数据库名称");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[DbCache] 获取数据库名称列表失败");
            }
            return dbNames;
        }

        /// <summary>
        /// 获取当前服务器上所有缓存的数据库名称列表。
        /// 优先使用 _allDatabaseNames，若未就绪则从缓存 key 降级提取，
        /// 最后从 cache/ 目录分文件扫描。
        /// </summary>
        public List<string> GetAllDatabaseNames()
        {
            if (_allDatabaseNames != null && _allDatabaseNames.Count > 0)
                return _allDatabaseNames;

            // ★ 降级1：从缓存 key 中提取当前服务器的数据库名称
            var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var serverPrefix = _currentServer != null ? $"{_currentServer}." : "";
            foreach (var key in _cache.Keys)
            {
                if (_currentServer != null && !key.StartsWith(serverPrefix, StringComparison.OrdinalIgnoreCase))
                    continue;
                var parts = key.Split('.');
                if (parts.Length >= 2)
                    extracted.Add(parts[parts.Length - 1]);
            }

            // ★ 降级2：从 cache/ 目录扫描分文件名提取 DB 名称
            var fromFiles = ScanCacheFilesForServer(_currentServer);
            foreach (var db in fromFiles)
                extracted.Add(db);

            if (extracted.Count > 0)
                return extracted.OrderBy(n => n).ToList();

            return new List<string>();
        }

        /// <summary>
        /// 扫描 cache/ 目录中指定服务器的分文件，从文件名提取 DB 名称。
        /// 文件名格式：{safeServer}_{dbName}_object_cache.json
        /// </summary>
        private List<string> ScanCacheFilesForServer(string? server)
        {
            var result = new List<string>();
            if (string.IsNullOrEmpty(server)) return result;

            try
            {
                var baseDir = Path.GetDirectoryName(
                    System.Reflection.Assembly.GetExecutingAssembly().Location)!;
                var cacheDir = Path.Combine(baseDir, "cache");
                if (!Directory.Exists(cacheDir)) return result;

                var safeServer = server!
                    .Replace('.', '_').Replace(':', '_').Replace('\\', '_').Replace('/', '_');
                var prefix = $"{safeServer}_";
                var suffix = "_object_cache.json";

                foreach (var file in Directory.GetFiles(cacheDir, $"{prefix}*{suffix}"))
                {
                    var fileName = Path.GetFileNameWithoutExtension(file);
                    // 去掉前缀和后缀，得到 DB 名称
                    var dbName = fileName.Substring(prefix.Length, fileName.Length - prefix.Length - "_object_cache".Length);
                    if (!string.IsNullOrEmpty(dbName))
                        result.Add(dbName);
                }

                if (result.Count > 0)
                    _logger.Information($"[DbCache] 从缓存目录扫描到 {result.Count} 个数据库: {server}");
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DbCache] 扫描缓存目录失败: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// 获取当前服务器上所有数据库的名称（作为 CompletionCandidate 列表）。
        /// </summary>
        public List<CompletionCandidate> GetDatabaseCandidates()
        {
            var dbNames = _allDatabaseNames;
            var currentDb = _currentDatabase;
            var currentServer = _currentServer;

            // ★ 连接获取失败（上下文已清空）→ 不返回任何 DB 候选，防止补全显示旧连接的 DB 名称
            if (currentServer == null && _allDatabaseNames == null && _cache.Count == 0)
                return new List<CompletionCandidate>();

            // ★ 降级1：从缓存 key 中提取
            if (dbNames == null || dbNames.Count == 0)
            {
                var extracted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                var serverPrefix = currentServer != null ? $"{currentServer}." : "";
                foreach (var key in _cache.Keys)
                {
                    // ★ Bug 修复：只提取当前服务器的 DB 名称，避免混入其他服务器的 DB
                    if (currentServer != null && !key.StartsWith(serverPrefix, StringComparison.OrdinalIgnoreCase))
                        continue;
                    var parts = key.Split('.');
                    if (parts.Length >= 2)
                        extracted.Add(parts[parts.Length - 1]);
                }
                if (extracted.Count > 0)
                    dbNames = extracted.OrderBy(n => n).ToList();
            }

            if (dbNames == null || dbNames.Count == 0)
            {
                // ★ 降级2：从 cache/ 目录扫描分文件名
                dbNames = ScanCacheFilesForServer(currentServer);
            }

            if (dbNames == null || dbNames.Count == 0)
                return new List<CompletionCandidate>();

            return dbNames
                .Where(db => !string.Equals(db, currentDb, StringComparison.OrdinalIgnoreCase))
                .Select(db => new CompletionCandidate
                {
                    DisplayText = db,
                    FilterText = db,
                    TypeTag = "DB",
                    TypeCode = "DB",
                    InsertText = $"[{db}].",
                    ItemType = CompletionItemType.Database,
                    MatchScore = 0,
                })
                .ToList();
        }

        // ============================================================
        // 自动加载（连接检测 + 按需拉取）
        // ============================================================

        /// <summary>
        /// 检测当前连接的服务器是否已切换（用户在不切换文档的情况下更改了连接）。
        /// 如果切换，更新 _currentServer/_currentDatabase 并从缓存文件加载新服务器数据。
        /// 此方法应被补全服务在显示候选前调用。
        /// </summary>
        public async Task DetectServerSwitchAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var connInfo = await _connectionService.GetConnectionInfoAsync();
                if (connInfo == null || string.IsNullOrEmpty(connInfo.ConnectionString))
                {
                    // ★ 问题修复：切换新连接但获取不到连接串时，静默返回用户无感知
                    // 1. 清空当前连接上下文，防止补全弹窗继续显示旧连接的数据库对象
                    // 2. 右下角 Toast 提示（30 秒节流），引导用户确认连接
                    _logger.Warning("[DbCache] DetectServerSwitch: 未获取到连接信息（ConnectionString 为空）");
                    ClearCurrentContext();
                    ShowConnectionFailedToast(
                        "未获取到当前查询窗口的连接串信息，无法加载该数据库的对象缓存，代码补全将暂不显示数据库对象。\n请确认已在 SSMS 中成功连接目标服务器（新建查询/切换连接后重试）。");
                    return;
                }

                var server = connInfo.ServerInstance ?? "unknown";
                var database = connInfo.DatabaseName ?? "unknown";

                if (_currentServer != null && !string.Equals(_currentServer, server, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Information($"[DbCache] DetectServerSwitch: '{_currentServer}' → '{server}'，重置并加载新服务器缓存");
                    _allDatabaseNames = null;
                    Interlocked.Exchange(ref _allDatabasesFetched, 0);
                    _currentServer = server;
                    _currentDatabase = database;
                    _currentCacheKey = $"{server}.{database}";
                    InvalidateLru(); // US-07: 清空 LRU 结果缓存

                    // 尝试从本地分文件加载当前 DB 缓存
                    var cachedFromFile = LoadCacheFromFile(server, database);
                    if (cachedFromFile != null)
                    {
                        var cacheKey = $"{server}.{database}";
                        _cache[cacheKey] = cachedFromFile;
                        _currentObjects = cachedFromFile.Objects;
                        _logger.Information($"[DbCache] ✅ DetectServerSwitch: 从本地文件加载缓存: {cacheKey} ({cachedFromFile.Objects.Count} 对象)");
                    }

                    // 从缓存目录扫描 DB 名称列表（快速，不需要连接数据库）
                    var dbNames = ScanCacheFilesForServer(server);
                    if (dbNames.Count > 0)
                    {
                        _allDatabaseNames = dbNames;
                        _logger.Information($"[DbCache] DetectServerSwitch: 从缓存目录获取到 {dbNames.Count} 个数据库名称");
                    }

                    // 后台异步获取完整的 DB 名称列表 + 预缓存
                    if (Interlocked.CompareExchange(ref _allDatabasesFetched, 1, 0) == 0)
                    {
                        var taskServer = server;
                        _ = Task.Run(async () =>
                        {
                            try
                            {
                                var allDbNames = await FetchAllDatabaseNamesAsync(connInfo.ConnectionString!);
                                if (string.Equals(_currentServer, taskServer, StringComparison.OrdinalIgnoreCase))
                                {
                                    _allDatabaseNames = allDbNames;
                                    _logger.Information($"[DbCache] DetectServerSwitch: 已获取 {allDbNames.Count} 个数据库名称，开始预缓存...");
                                    await PrefetchAllDatabasesAsync(connInfo.ConnectionString!, taskServer, allDbNames);
                                }
                            }
                            catch (Exception ex)
                            {
                                _logger.Error(ex, "[DbCache] DetectServerSwitch: 全量预加载失败");
                                Interlocked.Exchange(ref _allDatabasesFetched, 0);
                            }
                        });
                    }
                }
            }
            catch (Exception ex)
            {
                // ★ 问题修复：连接获取异常（如所有 provider 失败抛 AggregateException）时
                // 同样清空上下文 + Toast，避免补全继续使用旧连接的数据库对象
                _logger.Warning($"[DbCache] DetectServerSwitch 失败: {ex.Message}");
                ClearCurrentContext();
                ShowConnectionFailedToast(
                    $"获取当前查询窗口的连接信息失败：{ex.Message}\n代码补全将暂不显示数据库对象，请确认已在 SSMS 中成功连接目标服务器。");
            }
        }

        // ============================================================
        // 连接失败 Toast 提示
        // ============================================================

        /// <summary>
        /// 清空当前连接上下文（连接获取失败时调用）。
        /// 防止补全弹窗继续显示旧连接的数据库对象（表/视图/SP/DB名称等）。
        /// </summary>
        private void ClearCurrentContext()
        {
            _currentObjects = null;
            _currentCacheKey = null;
            _currentServer = null;
            _currentDatabase = null;
            _allDatabaseNames = null;
            _cache.Clear(); // ★ 清空所有内存缓存：GetDatabaseCandidates/GetMatchingObjects 的降级逻辑
                            //   会从 _cache 的 key 中提取 DB 名称，不清空则旧连接的 DB 仍会出现在补全中
            Interlocked.Exchange(ref _allDatabasesFetched, 0);
            Interlocked.Increment(ref _lruCacheVersion); // 使 LRU 结果缓存失效
            _logger.Warning("[DbCache] 已清空当前连接上下文（连接获取失败），补全将不再显示旧连接的数据库对象");
        }

        /// <summary>
        /// 在右下角显示连接失败 Toast 提示（30 秒节流，避免频繁弹出打扰用户）。
        /// 提示停留 10 秒后自动关闭，用户可点击 ✕ 手动关闭。
        /// </summary>
        private void ShowConnectionFailedToast(string message)
        {
            try
            {
                // 节流：同一窗口期只提示一次
                lock (_toastLock)
                {
                    var now = DateTime.UtcNow;
                    if ((now - _lastConnectionToastTime) < ConnectionToastThrottle)
                        return;
                    _lastConnectionToastTime = now;
                }

                if (!ThreadHelper.CheckAccess())
                {
                    ThreadHelper.JoinableTaskFactory.Run(async () =>
                    {
                        await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                        ShowConnectionFailedToastCore(message);
                    });
                }
                else
                {
                    ShowConnectionFailedToastCore(message);
                }
            }
            catch (Exception ex)
            {
                _logger?.Warning($"[DbCache] ShowConnectionFailedToast 失败: {ex.Message}");
            }
        }

        private void ShowConnectionFailedToastCore(string message)
        {
            try
            {
                var mainWindow = Application.Current?.MainWindow;
                var toast = new ConnectionToastWindow();
                toast.SetMessage("无法获取数据库连接", message);
                // ShowAtBottomRight 内部处理：有效主窗口 → 其右下角；否则 → 屏幕工作区右下角
                toast.ShowAtBottomRight(mainWindow);
                _logger.Information("[DbCache] 已显示连接失败 Toast 提示");
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DbCache] 显示连接失败 Toast 失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 尝试为当前连接自动加载缓存。
        /// 1. 获取服务器上所有数据库名称
        /// 2. 如果当前数据库在缓存中不存在，后台拉取
        /// 3. 其他数据库在需要时延迟加载（lazy loading）
        /// </summary>
        public async Task TryAutoLoadAsync()
        {
            // 防止并发自动加载
            if (Interlocked.CompareExchange(ref _isAutoLoading, 1, 0) != 0)
                return;

            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var connInfo = await _connectionService.GetConnectionInfoAsync();
                if (connInfo == null || string.IsNullOrEmpty(connInfo.ConnectionString))
                {
                    // ★ 问题修复：自动加载时获取不到连接串，Toast 提示用户（30 秒节流）
                    // 同时清空当前连接上下文，防止补全显示旧连接对象
                    _logger.Warning("[DbCache] TryAutoLoad: 未获取到连接信息（ConnectionString 为空）");
                    ClearCurrentContext();
                    ShowConnectionFailedToast(
                        "无法加载当前页面的数据库对象信息，代码补全将暂不显示数据库对象。\n请确认已在 SSMS对象资源管理器 中成功连接目标服务器。");
                    return;
                }

                var server = connInfo.ServerInstance ?? "unknown";
                var database = connInfo.DatabaseName ?? "unknown";
                var cacheKey = $"{server}.{database}";

                // ★ Bug 修复：检测服务器切换，重置 DB 名称列表
                if (_currentServer != null && !string.Equals(_currentServer, server, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Information($"[DbCache] 检测到服务器切换: '{_currentServer}' → '{server}'，重置 DB 名称列表");
                    _allDatabaseNames = null;
                    Interlocked.Exchange(ref _allDatabasesFetched, 0);
                }

                // 更新当前上下文
                _currentServer = server;
                _currentDatabase = database;
                _currentCacheKey = cacheKey;
                InvalidateLru(); // US-07: 清空 LRU 结果缓存

                // ★ 尝试从本地分文件加载当前 DB 缓存（快速启动）
                var cachedFromFile = LoadCacheFromFile(server, database);
                if (cachedFromFile != null)
                {
                    _cache[cacheKey] = cachedFromFile;
                    _currentObjects = cachedFromFile.Objects;
                    _logger.Information($"[DbCache] ✅ 从本地文件加载缓存: {cacheKey} ({cachedFromFile.Objects.Count} 对象)");

                    // 后台异步刷新（确保数据最新，不阻塞UI）
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            await FetchAndCacheAsync(connInfo.ConnectionString!, server, database, isAutoLoad: true);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "[DbCache] 后台刷新失败");
                        }
                    });
                    return;
                }

                // 本地无缓存或已过期 → 从数据库拉取
                if (_cache.TryGetValue(cacheKey, out var existingCache))
                {
                    _currentObjects = existingCache.Objects;
                    _logger.Debug($"[DbCache] 缓存已存在: {cacheKey} ({existingCache.Objects.Count} 对象)");
                }
                else
                {
                    // 缓存不存在，后台拉取当前 DB
                    _currentObjects = null;
                    StatusChanged?.Invoke($"正在加载 {database} 的数据库对象...");
                    _ = Task.Run(() => FetchAndCacheAsync(connInfo.ConnectionString!, server, database, isAutoLoad: true));
                }

                // ★ 全量预加载：获取所有数据库名称 + 预缓存所有 DB 的对象
                if (Interlocked.CompareExchange(ref _allDatabasesFetched, 1, 0) == 0)
                {
                    var taskServer = server; // 捕获当前服务器名，防止竞态
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var dbNames = await FetchAllDatabaseNamesAsync(connInfo.ConnectionString!);
                            // ★ Bug 修复：检查服务器是否已切换，避免旧服务器的异步任务覆盖 _allDatabaseNames
                            if (!string.Equals(_currentServer, taskServer, StringComparison.OrdinalIgnoreCase))
                            {
                                _logger.Information($"[DbCache] 全量预加载：服务器已切换 '{taskServer}' → '{_currentServer}'，丢弃旧结果");
                                Interlocked.Exchange(ref _allDatabasesFetched, 0);
                                return;
                            }
                            _allDatabaseNames = dbNames;
                            _logger.Information($"[DbCache] 已获取 {dbNames.Count} 个数据库名称，开始预缓存...");

                            // ★ 并行预缓存所有数据库的对象
                            await PrefetchAllDatabasesAsync(connInfo.ConnectionString!, taskServer, dbNames);
                        }
                        catch (Exception ex)
                        {
                            _logger.Error(ex, "[DbCache] 全量预加载失败");
                            Interlocked.Exchange(ref _allDatabasesFetched, 0);
                        }
                    });
                }
            }
            catch (Exception ex)
            {
                // ★ 问题修复：自动加载异常时同样清空上下文 + Toast
                _logger.Error(ex, "[DbCache] 自动加载失败");
                ClearCurrentContext();
                ShowConnectionFailedToast(
                    $"加载数据库连接信息失败：{ex.Message}\n代码补全将暂不显示数据库对象，请确认已在 SSMS 中成功连接目标服务器。");
            }
            finally
            {
                Interlocked.Exchange(ref _isAutoLoading, 0);
            }
        }

        // ============================================================
        // 手动刷新（覆盖旧缓存）
        // ============================================================

        /// <summary>
        /// 为当前连接强制刷新缓存（覆盖旧数据）。
        /// 刷新当前服务器上所有数据库的对象列表。
        /// 由"刷新缓存"菜单命令调用。
        /// </summary>
        public async Task<bool> RefreshCacheForCurrentConnectionAsync()
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                var connInfo = await _connectionService.GetConnectionInfoAsync();
                if (connInfo == null || string.IsNullOrEmpty(connInfo.ConnectionString))
                {
                    _logger.Warning("[DbCache] 刷新缓存失败：未检测到数据库连接");
                    StatusChanged?.Invoke("未检测到数据库连接");
                    return false;
                }

                var server = connInfo.ServerInstance ?? "unknown";
                var database = connInfo.DatabaseName ?? "unknown";

                _currentServer = server;
                _currentDatabase = database;
                _currentCacheKey = $"{server}.{database}";
                InvalidateLru(); // US-07: 清空 LRU 结果缓存

                // ★ 重新获取数据库名称列表
                StatusChanged?.Invoke("正在获取服务器上所有数据库名称...");
                var dbNames = await FetchAllDatabaseNamesAsync(connInfo.ConnectionString!);
                _allDatabaseNames = dbNames;

                // ★ 刷新所有数据库的缓存
                StatusChanged?.Invoke($"正在刷新 {dbNames.Count} 个数据库的对象...");
                await PrefetchAllDatabasesAsync(connInfo.ConnectionString!, server, dbNames);

                // 更新当前 DB 的快速引用
                var currentCacheKey = $"{server}.{database}";
                if (_cache.TryGetValue(currentCacheKey, out var currentCache))
                {
                    _currentObjects = currentCache.Objects;
                }

                StatusChanged?.Invoke($"✅ 已刷新 {_cache.Count} 个数据库的缓存");
                return true;
            }
            catch (Exception ex)
            {
                _logger.Error(ex, "[DbCache] 刷新缓存失败");
                StatusChanged?.Invoke($"❌ 刷新失败: {ex.Message}");
                return false;
            }
        }

        // ============================================================
        // 核心拉取逻辑
        // ============================================================

        /// <summary>
        /// 预缓存服务器上所有数据库的对象（启动时全量加载，非懒惰加载）。
        /// 并行拉取，使用信号量控制并发数（最多3个同时查询）。
        /// 跳过已有缓存的数据库（文件缓存优先）。
        /// </summary>
        private async Task PrefetchAllDatabasesAsync(string connectionString, string server, List<string> dbNames)
        {
            var tasks = new List<Task>();
            int toFetch = 0;

            foreach (var db in dbNames)
            {
                var cacheKey = $"{server}.{db}";
                if (!_cache.ContainsKey(cacheKey))
                {
                    toFetch++;
                    tasks.Add(Task.Run(async () =>
                    {
                        await _fetchSemaphore.WaitAsync();
                        try
                        {
                            await FetchAndCacheAsync(connectionString, server, db, isAutoLoad: true);
                        }
                        finally
                        {
                            _fetchSemaphore.Release();
                        }
                    }));
                }
            }

            if (tasks.Count > 0)
            {
                _logger.Information($"[DbCache] 开始预缓存 {toFetch} 个数据库的对象（并发上限 3）...");
                await Task.WhenAll(tasks);
                _logger.Information($"[DbCache] ✅ 预缓存完成，共 {_cache.Count} 个数据库");
            }
            else
            {
                _logger.Information($"[DbCache] 所有数据库已有缓存，无需预拉取");
            }
        }

        /// <summary>
        /// 确保指定数据库的缓存已加载（懒惰加载兜底）。
        /// 如果已全量加载则直接返回，否则从数据库拉取。
        /// </summary>
        public async Task EnsureDatabaseLoadedAsync(string databaseName)
        {
            // ★ Bug 修复：先检测当前服务器，避免使用过期的 _currentServer
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            var connInfo = await _connectionService.GetConnectionInfoAsync();
            if (connInfo == null || string.IsNullOrEmpty(connInfo.ConnectionString))
                return;

            var server = connInfo.ServerInstance ?? "unknown";

            // ★ 检测服务器切换（用户可能在不切换文档的情况下更改了连接）
            if (_currentServer != null && !string.Equals(_currentServer, server, StringComparison.OrdinalIgnoreCase))
            {
                _logger.Information($"[DbCache] EnsureDatabaseLoaded: 检测到服务器切换: '{_currentServer}' → '{server}'，重置 DB 名称列表");
                _allDatabaseNames = null;
                Interlocked.Exchange(ref _allDatabasesFetched, 0);
            }

            _currentServer = server;
            _currentDatabase = databaseName;

            var cacheKey = $"{server}.{databaseName}";
            if (_cache.ContainsKey(cacheKey))
                return;

            StatusChanged?.Invoke($"正在加载 {databaseName} 的对象...");
            _logger.Information($"[DbCache] 懒惰加载数据库缓存: {databaseName} (server={server})");

            await FetchAndCacheAsync(connInfo.ConnectionString!, server, databaseName, isAutoLoad: true);
        }

        /// <summary>
        /// 强制刷新指定数据库的缓存（覆盖旧数据，包含列和参数信息）。
        /// 用于检测到缓存缺少 Columns/Parameters 时触发。
        /// </summary>
        public async Task RefreshSingleDatabaseAsync(string databaseName)
        {
            try
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                var connInfo = await _connectionService.GetConnectionInfoAsync();
                if (connInfo == null || string.IsNullOrEmpty(connInfo.ConnectionString))
                    return;

                // ★ Bug 修复：从连接信息检测当前服务器，避免使用过期的 _currentServer
                var server = connInfo.ServerInstance ?? "unknown";
                if (_currentServer != null && !string.Equals(_currentServer, server, StringComparison.OrdinalIgnoreCase))
                {
                    _logger.Information($"[DbCache] RefreshSingle: 检测到服务器切换: '{_currentServer}' → '{server}'，重置 DB 名称列表");
                    _allDatabaseNames = null;
                    Interlocked.Exchange(ref _allDatabasesFetched, 0);
                }
                _currentServer = server;
                _currentDatabase = databaseName;

                StatusChanged?.Invoke($"正在刷新 {databaseName} 的对象...");
                _logger.Information($"[DbCache] 强制刷新数据库缓存: {databaseName} (server={server})");

                await FetchAndCacheAsync(connInfo.ConnectionString!, server, databaseName, isAutoLoad: true);
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"[DbCache] 强制刷新失败: {databaseName}");
            }
        }

        /// <summary>
        /// 获取当前连接的数据库名称。
        /// </summary>
        public string? GetCurrentDatabaseName() => _currentDatabase;

        /// <summary>
        /// 从数据库拉取对象列表并缓存。
        /// </summary>
        private async Task FetchAndCacheAsync(
            string connectionString,
            string server,
            string database,
            bool isAutoLoad)
        {
            var cacheKey = $"{server}.{database}";

            try
            {
                var objects = await QueryDatabaseObjectsAsync(connectionString, database);
                var schemas = await QueryDatabaseSchemasAsync(connectionString, database);
                var columns = await QueryColumnsAsync(connectionString, database);
                var parameters = await QueryParametersAsync(connectionString, database);
                var indexes = await QueryIndexesAsync(connectionString, database);

                var cache = new DatabaseObjectCache
                {
                    Server = server,
                    Database = database,
                    LastUpdated = DateTime.UtcNow,
                    Objects = objects,
                    Schemas = schemas,
                };

                // 关联列信息到对象
                foreach (var obj in objects)
                {
                    if (obj.TypeCode is "U" or "V")
                    {
                        obj.Columns = columns.TryGetValue($"{obj.Schema}.{obj.Name}", out var cols)
                            ? cols : null;
                        // ★ 关联索引信息
                        obj.Indexes = indexes.TryGetValue($"{obj.Schema}.{obj.Name}", out var idxs)
                            ? idxs : null;
                    }
                    if (obj.TypeCode is "P" or "FN" or "IF" or "TF")
                    {
                        obj.Parameters = parameters.TryGetValue($"{obj.Schema}.{obj.Name}", out var ps)
                            ? ps : null;
                    }
                }

                // ★ 构建所有运行时索引（前缀索引 + 类型索引 + NameLower + 预排序）
                cache.RebuildPrefixIndex();

                _cache[cacheKey] = cache;

                // 如果当前上下文仍是这个连接，更新快速引用
                if (_currentCacheKey == cacheKey)
                {
                    _currentObjects = objects;
                }

                // ★ 仅保存到分文件（按 DB 独立文件），避免单文件资源争抢
                SaveCacheToFile(cache);

                var prefix = isAutoLoad ? "[DbCache] 自动加载" : "[DbCache] 手动刷新";
                _logger.Information($"{prefix} 完成: {cacheKey} → {objects.Count} 个对象");
                StatusChanged?.Invoke($"✅ {database} 已缓存 {objects.Count} 个对象");
            }
            catch (Exception ex)
            {
                _logger.Error(ex, $"[DbCache] 拉取数据库对象失败: {cacheKey}");
                StatusChanged?.Invoke($"❌ {database} 加载失败: {ex.Message}");
            }
        }

        /// <summary>
        /// 查询指定数据库中的用户对象（表、视图、SP、函数）。
        /// 使用 USE [database] 切换到目标库。
        /// </summary>
        private async Task<List<DatabaseObjectInfo>> QueryDatabaseObjectsAsync(string connectionString, string database)
        {
            var results = new List<DatabaseObjectInfo>();

            // 切换数据库查询
            var sql = $@"USE [{database}];
SELECT 
    s.name AS SchemaName,
    o.name AS ObjectName,
    o.type AS TypeCode
FROM sys.objects o
INNER JOIN sys.schemas s ON o.schema_id = s.schema_id
WHERE o.type IN ('U', 'V', 'P', 'FN', 'IF', 'TF')
  AND o.is_ms_shipped = 0
ORDER BY o.type, s.name, o.name";

            using var conn = new SqlConnection(connectionString);
            await conn.OpenAsync();

            using var cmd = new SqlCommand(sql, conn);
            cmd.CommandTimeout = 30;

            using var reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                var schema = reader["SchemaName"]?.ToString() ?? "";
                var name = reader["ObjectName"]?.ToString() ?? "";
                var typeCode = (reader["TypeCode"]?.ToString() ?? "").Trim();

                results.Add(new DatabaseObjectInfo
                {
                    Schema = schema,
                    Name = name,
                    TypeCode = typeCode,
                    TypeName = GetTypeTagName(typeCode)
                });
            }

            return results;
        }

        /// <summary>
        /// 查询指定数据库的所有 Schema 名称。
        /// </summary>
        private async Task<List<string>> QueryDatabaseSchemasAsync(string connectionString, string database)
        {
            var schemas = new List<string>();
            try
            {
                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand(
                    $@"USE [{database}];
SELECT name FROM sys.schemas WHERE name NOT LIKE 'db\_%' ESCAPE '\' ORDER BY name", conn);
                cmd.CommandTimeout = 10;
                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    schemas.Add(reader.GetString(0));
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DbCache] 查询 Schema 列表失败 {database}: {ex.Message}");
                // 失败时使用默认列表
                schemas = new List<string> { "dbo", "sys", "guest", "INFORMATION_SCHEMA" };
            }
            return schemas;
        }

        /// <summary>
        /// 批量查询指定数据库的所有表/视图的列信息。
        /// Key = "schema.TableName", Value = 列信息列表
        /// </summary>
        private async Task<Dictionary<string, List<ColumnInfo>>> QueryColumnsAsync(
            string connectionString, string database)
        {
            var result = new Dictionary<string, List<ColumnInfo>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand($@"USE [{database}];
SELECT
    OBJECT_SCHEMA_NAME(c.object_id) AS SchemaName,
    OBJECT_NAME(c.object_id) AS TableName,
    c.name AS ColumnName,
    TYPE_NAME(c.user_type_id) AS DataType,
    c.max_length, c.precision, c.scale,
    c.is_nullable, c.is_identity,
    c.is_computed,
    cc.definition AS ComputedDefinition,
    c.default_object_id,
    OBJECT_DEFINITION(c.default_object_id) AS DefaultDefinition,
    ep.value AS Description
FROM sys.columns c
LEFT JOIN sys.computed_columns cc ON c.object_id = cc.object_id AND c.column_id = cc.column_id
LEFT JOIN sys.extended_properties ep
    ON ep.major_id = c.object_id AND ep.minor_id = c.column_id AND ep.name = 'MS_Description'
WHERE c.object_id IN (
    SELECT object_id FROM sys.objects WHERE type IN ('U','V')
)
ORDER BY c.object_id, c.column_id", conn);
                cmd.CommandTimeout = 30;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var schema = reader["SchemaName"]?.ToString() ?? "";
                    var tableName = reader["TableName"]?.ToString() ?? "";
                    var key = $"{schema}.{tableName}";

                    if (!result.TryGetValue(key, out var list))
                    {
                        list = new List<ColumnInfo>();
                        result[key] = list;
                    }
                    list.Add(new ColumnInfo
                    {
                        Name = reader["ColumnName"]?.ToString() ?? "",
                        DataType = reader["DataType"]?.ToString() ?? "",
                        MaxLength = reader["max_length"] != DBNull.Value ? Convert.ToInt16(reader["max_length"]) : (short)-1,
                        Precision = reader["precision"] != DBNull.Value ? Convert.ToByte(reader["precision"]) : (byte)0,
                        Scale = reader["scale"] != DBNull.Value ? Convert.ToByte(reader["scale"]) : (byte)0,
                        IsNullable = reader["is_nullable"] != DBNull.Value && Convert.ToBoolean(reader["is_nullable"]),
                        IsIdentity = reader["is_identity"] != DBNull.Value && Convert.ToBoolean(reader["is_identity"]),
                        IsComputed = reader["is_computed"] != DBNull.Value && Convert.ToBoolean(reader["is_computed"]),
                        ComputedDefinition = reader["ComputedDefinition"]?.ToString(),
                        HasDefault = reader["default_object_id"] != DBNull.Value && Convert.ToInt32(reader["default_object_id"]) != 0,
                        DefaultDefinition = reader["DefaultDefinition"]?.ToString(),
                        DefaultValue = reader["DefaultDefinition"]?.ToString(),
                        Description = reader["Description"]?.ToString(),
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DbCache] 查询列信息失败 {database}: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// 批量查询指定数据库的所有表/视图的索引信息。
        /// Key = "schema.TableName", Value = 索引信息列表
        /// </summary>
        private async Task<Dictionary<string, List<IndexInfo>>> QueryIndexesAsync(
            string connectionString, string database)
        {
            var result = new Dictionary<string, List<IndexInfo>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand($@"USE [{database}];
SELECT
    OBJECT_SCHEMA_NAME(i.object_id) AS SchemaName,
    OBJECT_NAME(i.object_id) AS TableName,
    i.name AS IndexName,
    i.type_desc AS IndexType,
    i.is_unique AS IsUnique,
    i.is_primary_key AS IsPrimaryKey,
    i.is_unique_constraint AS IsUniqueConstraint,
    STUFF((
        SELECT ', ' + c2.name + CASE WHEN ic2.is_descending_key = 1 THEN ' DESC' ELSE ' ASC' END
        FROM sys.index_columns ic2
        JOIN sys.columns c2 ON ic2.object_id = c2.object_id AND ic2.column_id = c2.column_id
        WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id
          AND ic2.is_included_column = 0
        ORDER BY ic2.key_ordinal
        FOR XML PATH(''), TYPE
    ).value('.', 'NVARCHAR(MAX)'), 1, 2, '') AS KeyColumns,
    STUFF((
        SELECT ', ' + c2.name
        FROM sys.index_columns ic2
        JOIN sys.columns c2 ON ic2.object_id = c2.object_id AND ic2.column_id = c2.column_id
        WHERE ic2.object_id = i.object_id AND ic2.index_id = i.index_id
          AND ic2.is_included_column = 1
        ORDER BY ic2.index_column_id
        FOR XML PATH(''), TYPE
    ).value('.', 'NVARCHAR(MAX)'), 1, 2, '') AS IncludedColumns,
    i.has_filter,
    i.filter_definition
FROM sys.indexes i
WHERE i.object_id IN (SELECT object_id FROM sys.objects WHERE type IN ('U','V'))
  AND i.name IS NOT NULL
  AND i.type > 0
ORDER BY i.object_id, i.is_primary_key DESC, i.is_unique DESC, i.name", conn);
                cmd.CommandTimeout = 30;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var schema = reader["SchemaName"]?.ToString() ?? "";
                    var tableName = reader["TableName"]?.ToString() ?? "";
                    var key = $"{schema}.{tableName}";

                    if (!result.TryGetValue(key, out var list))
                    {
                        list = new List<IndexInfo>();
                        result[key] = list;
                    }
                    list.Add(new IndexInfo
                    {
                        Name = reader["IndexName"]?.ToString() ?? "",
                        TypeDesc = reader["IndexType"]?.ToString() ?? "",
                        IsUnique = reader["IsUnique"] != DBNull.Value && Convert.ToBoolean(reader["IsUnique"]),
                        IsPrimaryKey = reader["IsPrimaryKey"] != DBNull.Value && Convert.ToBoolean(reader["IsPrimaryKey"]),
                        IsUniqueConstraint = reader["IsUniqueConstraint"] != DBNull.Value && Convert.ToBoolean(reader["IsUniqueConstraint"]),
                        KeyColumns = reader["KeyColumns"]?.ToString() ?? "",
                        IncludedColumns = reader["IncludedColumns"]?.ToString(),
                        HasFilter = reader["has_filter"] != DBNull.Value && Convert.ToBoolean(reader["has_filter"]),
                        FilterDefinition = reader["filter_definition"]?.ToString(),
                    });
                }
                _logger.Information($"[DbCache] 查询索引完成 {database}: {result.Count} 个表有索引");
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DbCache] 查询索引信息失败 {database}: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// 批量查询指定数据库的所有 SP/函数的参数信息。
        /// Key = "schema.ProcName", Value = 参数信息列表
        /// </summary>
        private async Task<Dictionary<string, List<ParameterInfo>>> QueryParametersAsync(
            string connectionString, string database)
        {
            var result = new Dictionary<string, List<ParameterInfo>>(StringComparer.OrdinalIgnoreCase);
            try
            {
                using var conn = new SqlConnection(connectionString);
                await conn.OpenAsync();
                using var cmd = new SqlCommand($@"USE [{database}];
SELECT
    OBJECT_SCHEMA_NAME(p.object_id) AS SchemaName,
    OBJECT_NAME(p.object_id) AS ProcName,
    p.name AS ParameterName,
    TYPE_NAME(p.user_type_id) AS DataType,
    p.max_length, p.precision, p.scale,
    p.is_output, p.has_default_value, p.default_value
FROM sys.parameters p
WHERE p.object_id IN (
    SELECT object_id FROM sys.objects WHERE type IN ('P','FN','IF','TF')
)
ORDER BY p.object_id, p.parameter_id", conn);
                cmd.CommandTimeout = 30;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    var schema = reader["SchemaName"]?.ToString() ?? "";
                    var procName = reader["ProcName"]?.ToString() ?? "";
                    var key = $"{schema}.{procName}";

                    if (!result.TryGetValue(key, out var list))
                    {
                        list = new List<ParameterInfo>();
                        result[key] = list;
                    }
                    list.Add(new ParameterInfo
                    {
                        Name = reader["ParameterName"]?.ToString() ?? "",
                        DataType = reader["DataType"]?.ToString() ?? "",
                        MaxLength = reader["max_length"] != DBNull.Value ? Convert.ToInt16(reader["max_length"]) : (short)-1,
                        IsOutput = reader["is_output"] != DBNull.Value && Convert.ToBoolean(reader["is_output"]),
                        HasDefault = reader["has_default_value"] != DBNull.Value && Convert.ToBoolean(reader["has_default_value"]),
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.Warning($"[DbCache] 查询SP参数失败 {database}: {ex.Message}");
            }
            return result;
        }

        /// <summary>
        /// 将 SQL Server 类型代码映射为中文标签。
        /// </summary>
        private static string GetTypeTagName(string typeCode)
        {
            return (typeCode ?? "").Trim() switch
            {
                "U" => "表",
                "V" => "视图",
                "P" => "SP",
                "FN" or "IF" or "TF" => "函数",
                _ => "对象"
            };
        }

        // ============================================================
        // 前缀索引
        // ============================================================

        /// <summary>
        /// 为指定对象列表构建前缀索引。
        /// 每个对象的前 1~3 个字符作为 key，value 为匹配的对象列表。
        /// </summary>
        internal static Dictionary<string, List<DatabaseObjectInfo>> BuildPrefixIndex(List<DatabaseObjectInfo> objects)
        {
            var index = new Dictionary<string, List<DatabaseObjectInfo>>();
            if (objects == null || objects.Count == 0)
                return index;

            foreach (var obj in objects)
            {
                var lowerName = obj.Name.ToLowerInvariant();
                int maxLen = Math.Min(3, lowerName.Length);
                for (int len = 1; len <= maxLen; len++)
                {
                    var prefix = lowerName.Substring(0, len);
                    if (!index.TryGetValue(prefix, out var list))
                    {
                        list = new List<DatabaseObjectInfo>();
                        index[prefix] = list;
                    }
                    list.Add(obj);
                }
            }
            return index;
        }

        /// <summary>
        /// 轻量子序列匹配：检查 input 的所有字符是否按顺序出现在 target 中。
        /// 逐字符比较，无字符串分配，比 FuzzyMatch 快得多。
        /// US-05: 使用预计算的 NameLower，避免逐字符 char.ToLowerInvariant。
        /// </summary>
        private static bool IsSubsequence(string inputLower, string targetLower)
        {
            int inputIdx = 0;
            int inputLen = inputLower.Length;
            int targetLen = targetLower.Length;

            for (int i = 0; i < targetLen && inputIdx < inputLen; i++)
            {
                if (targetLower[i] == inputLower[inputIdx])
                {
                    inputIdx++;
                }
            }

            return inputIdx == inputLen;
        }

        // ============================================================
        // 补全查询（当前 DB）
        // ============================================================

        /// <summary>
        /// 为数据库对象创建统一的 CompletionCandidate。
        /// 跨库对象在 DisplayText 中带上 [DBName] 前缀，便于区分。
        /// </summary>
        private static CompletionCandidate CreateObjectCandidate(DatabaseObjectInfo o, string prefix, string? dbName, int? preComputedScore = null)
        {
            var score = preComputedScore ?? (string.IsNullOrEmpty(prefix) ? 0 : CompletionCandidate.FuzzyMatch(prefix, o.Name));
            return new CompletionCandidate
            {
                DisplayText = dbName != null ? $"[{dbName}].[{o.Schema}].[{o.Name}]" : $"{o.Schema}.{o.Name}",
                FilterText = o.Name,
                TypeTag = GetTypeTagName(o.TypeCode),
                TypeCode = (o.TypeCode ?? "").Trim(),
                InsertText = $"[{o.Schema}].[{o.Name}]",
                DatabaseName = dbName,
                ItemType = CompletionItemType.DatabaseObject,
                Source = o,
                MatchScore = score,
            };
        }


        /// <summary>
        /// 在指定对象列表和前缀索引中执行搜索。
        /// 优化版本（US-01/03/04/05）：
        /// 1. 空前缀：限量 Take(250) + 免 OrderBy（依赖预排序）
        /// 2. 前缀命中 ≥ 250：跳过子序列补充 + 跳过 FuzzyMatch，直接公式算分
        /// 3. 前缀命中 < 250：前缀命中公式算分，仅子序列补充走 FuzzyMatch
        /// 4. IsSubsequence 使用预计算 NameLower
        /// </summary>
        private static List<CompletionCandidate> SearchInCache(
            List<DatabaseObjectInfo> objects,
            Dictionary<string, List<DatabaseObjectInfo>> prefixIndex,
            string prefix,
            string? dbName = null)
        {
            if (objects == null || objects.Count == 0)
                return new List<CompletionCandidate>();

            // US-01/US-03：空前缀限量 Take(250) + 免 OrderBy（Objects 已预排序）
            if (string.IsNullOrEmpty(prefix))
            {
                return objects
                    .Take(MaxCompletionCandidates)
                    .Select(o => CreateObjectCandidate(o, prefix, dbName))
                    .ToList();
            }

            var lowerPrefix = prefix.ToLowerInvariant();

            // 从前缀索引定位候选集
            List<DatabaseObjectInfo>? candidates = null;
            if (lowerPrefix.Length <= 3)
            {
                prefixIndex.TryGetValue(lowerPrefix, out candidates);
            }
            else
            {
                var threeCharPrefix = lowerPrefix.Substring(0, 3);
                prefixIndex.TryGetValue(threeCharPrefix, out var allThree);
                if (allThree != null)
                    candidates = allThree.Where(o =>
                        o.Name.StartsWith(lowerPrefix, StringComparison.OrdinalIgnoreCase)).ToList();
            }

            // US-04：前缀命中已 ≥ 上限 → 免子序列补充 + 免 FuzzyMatch
            if (candidates != null && candidates.Count >= MaxCompletionCandidates)
            {
                return candidates
                    .Take(MaxCompletionCandidates)
                    .Select(o =>
                    {
                        // 前缀命中必然走 FuzzyMatch 的 StartsWith 分支，分数公式固定
                        var score = 500 - (o.Name.Length - prefix.Length);
                        return (o, score);
                    })
                    .OrderByDescending(x => x.score)
                    .ThenBy(x => x.o.Name, StringComparer.OrdinalIgnoreCase)
                    .Select(x => CreateObjectCandidate(x.o, prefix, dbName, x.score))
                    .ToList();
            }

            // 子序列补充匹配：补充前缀匹配遗漏的结果（仅前缀命中 < 250 时执行）
            if (lowerPrefix.Length >= 3)
            {
                candidates ??= new List<DatabaseObjectInfo>();
                var existingSet = new HashSet<DatabaseObjectInfo>(candidates);
                int scanLimit = Math.Min(MaxCompletionCandidates, objects.Count);
                for (int i = 0; i < objects.Count && candidates.Count < scanLimit; i++)
                {
                    // US-05: 使用预计算 NameLower，避免逐字符 ToLowerInvariant
                    if (!existingSet.Contains(objects[i]) && IsSubsequence(lowerPrefix, objects[i].NameLower))
                    {
                        candidates.Add(objects[i]);
                        existingSet.Add(objects[i]);
                    }
                }
            }

            if (candidates == null || candidates.Count == 0)
                return new List<CompletionCandidate>();

            // US-04：混合排序——前缀命中公式算分，子序列补充走 FuzzyMatch
            // 用 existingSet 区分：前缀命中对象在 prefixIndex 中，补充对象不在
            var prefixHitSet = new HashSet<DatabaseObjectInfo>();
            if (lowerPrefix.Length <= 3)
            {
                prefixIndex.TryGetValue(lowerPrefix, out var prefixHits);
                if (prefixHits != null)
                    prefixHitSet = new HashSet<DatabaseObjectInfo>(prefixHits);
            }

            return candidates
                .Select(o =>
                {
                    int score;
                    if (prefixHitSet.Contains(o))
                    {
                        // 前缀命中：直接公式算分（免 ToLowerInvariant + 免完整 FuzzyMatch）
                        score = 500 - (o.Name.Length - prefix.Length);
                    }
                    else
                    {
                        // 子序列补充：走完整 FuzzyMatch
                        score = CompletionCandidate.FuzzyMatch(prefix, o.Name);
                    }
                    return (o, score);
                })
                .Where(x => x.score >= 0)
                .OrderByDescending(x => x.score)
                .ThenBy(x => x.o.Name, StringComparer.OrdinalIgnoreCase)
                .Select(x => CreateObjectCandidate(x.o, prefix, dbName, x.score))
                .ToList();
        }

        // ============================================================
        // US-07: 结果 LRU 缓存
        // ============================================================

        /// <summary>
        /// 从 LRU 缓存中查找结果。命中时返回候选列表，未命中返回 null。
        /// </summary>
        private List<CompletionCandidate>? TryGetLru(string key, int version)
        {
            lock (_lruLock)
            {
                if (version != _lruCacheVersion)
                    return null; // 缓存已切换，旧条目失效
                if (_lruCache.TryGetValue(key, out var result))
                {
                    // LRU: 移到链表头部
                    _lruKeys.Remove(key);
                    _lruKeys.AddFirst(key);
                    return result;
                }
                return null;
            }
        }

        /// <summary>
        /// 将结果存入 LRU 缓存，超出容量时淘汰最久未使用的条目。
        /// </summary>
        private void SetLru(string key, List<CompletionCandidate> value, int version)
        {
            lock (_lruLock)
            {
                if (version != _lruCacheVersion)
                {
                    // 缓存已切换，清空旧条目
                    _lruCache.Clear();
                    _lruKeys.Clear();
                    _lruCacheVersion = version;
                }

                if (_lruCache.ContainsKey(key))
                {
                    _lruKeys.Remove(key);
                }
                else if (_lruKeys.Count >= LruCapacity)
                {
                    // 淘汰最久未使用
                    var oldest = _lruKeys.Last.Value;
                    _lruKeys.RemoveLast();
                    _lruCache.Remove(oldest);
                }

                _lruKeys.AddFirst(key);
                _lruCache[key] = value;
            }
        }

        /// <summary>
        /// 清空 LRU 缓存（缓存切换/重建时调用）。
        /// </summary>
        private void InvalidateLru()
        {
            lock (_lruLock)
            {
                _lruCacheVersion++;
                _lruCache.Clear();
                _lruKeys.Clear();
            }
        }

        // ============================================================
        // 补全查询（当前 DB）
        // ============================================================

        /// <summary>
        /// 获取当前连接数据库中与指定前缀匹配的对象列表。
        /// US-07: 带 LRU 结果缓存。
        /// </summary>
        public List<CompletionCandidate> GetMatchingObjects(string prefix)
        {
            // US-07: LRU 缓存
            var lruKey = prefix ?? "";
            var lruVersion = _lruCacheVersion;
            var cached = TryGetLru(lruKey, lruVersion);
            if (cached != null)
                return cached;

            List<CompletionCandidate> result;
            if (_cache.TryGetValue(_currentCacheKey ?? "", out var cache))
            {
                result = SearchInCache(cache.Objects, cache.PrefixIndex, prefix, dbName: null);
            }

            // 回退到旧的 _currentObjects 路径（缓存未完全迁移时）
            else if (_currentObjects != null)
            {
                result = SearchInCache(_currentObjects, new Dictionary<string, List<DatabaseObjectInfo>>(), prefix, dbName: null);
            }
            else
            {
                result = new List<CompletionCandidate>();
            }

            SetLru(lruKey, result, lruVersion);
            return result;
        }

        /// <summary>
        /// 获取指定数据库中与指定前缀匹配的对象列表（跨库查询）。
        /// US-07: 带 LRU 结果缓存。
        /// 如果该数据库尚未缓存，返回空列表（由调用方触发懒惰加载）。
        /// </summary>
        public List<CompletionCandidate> GetMatchingObjectsForDb(string databaseName, string prefix)
        {
            if (_currentServer == null) return new List<CompletionCandidate>();
            var cacheKey = $"{_currentServer}.{databaseName}";

            // US-07: LRU 缓存（跨库用 dbName|prefix 作 key）
            var lruKey = $"{databaseName}|{prefix ?? ""}";
            var lruVersion = _lruCacheVersion;
            var cached = TryGetLru(lruKey, lruVersion);
            if (cached != null)
                return cached;

            List<CompletionCandidate> result;

            // 快速路径：缓存已就绪
            if (_cache.TryGetValue(cacheKey, out var cache))
            {
                result = SearchInCache(cache.Objects, cache.PrefixIndex, prefix, dbName: databaseName);
            }
            else
            {
                // ★ 慢路径：缓存正在后台加载中（首次启动时的 prefetch）
                result = new List<CompletionCandidate>();
                for (int i = 0; i < 30; i++)
                {
                    System.Threading.Thread.Sleep(50);
                    if (_cache.TryGetValue(cacheKey, out cache))
                    {
                        _logger.Debug($"[DbCache] 轮询等待 {databaseName} 缓存就绪: 耗时约 {(i + 1) * 50}ms");
                        result = SearchInCache(cache.Objects, cache.PrefixIndex, prefix, dbName: databaseName);
                        break;
                    }
                }

                if (result.Count == 0 && !_cache.ContainsKey(cacheKey))
                {
                    _logger.Warning($"[DbCache] 等待超时: {databaseName} 缓存未就绪");
                }
            }

            SetLru(lruKey, result, lruVersion);
            return result;
        }

        /// <summary>
        /// 获取当前连接数据库中指定类型的所有对象（无前缀过滤，空格触发）。
        /// US-02: 使用 typeIndex O(1) 定位，免全量遍历。
        /// US-01/03: 限量 Take(250) + 免 OrderBy（依赖预排序）。
        /// </summary>
        public List<CompletionCandidate> GetAllObjectsOfTypes(params string[] typeCodes)
        {
            // US-02: 优先从 typeIndex 快速定位
            if (_cache.TryGetValue(_currentCacheKey ?? "", out var cache) &&
                cache.TypeIndex != null && cache.TypeIndex.Count > 0)
            {
                var set = new HashSet<string>(typeCodes, StringComparer.OrdinalIgnoreCase);
                var filtered = cache.TypeIndex
                    .Where(kv => set.Contains(kv.Key))
                    .SelectMany(kv => kv.Value)
                    .ToList();
                return filtered
                    .Take(MaxCompletionCandidates)
                    .Select(o => CreateObjectCandidate(o, "", null))
                    .ToList();
            }

            // 回退到旧逻辑（缓存未完全迁移时）
            var objects = _currentObjects;
            if (objects == null || objects.Count == 0)
                return new List<CompletionCandidate>();

            var fallbackFiltered = objects
                .Where(o => typeCodes.Contains(o.TypeCode, StringComparer.OrdinalIgnoreCase))
                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .Take(MaxCompletionCandidates)
                .Select(o => CreateObjectCandidate(o, "", null))
                .ToList();
            return fallbackFiltered;
        }

        /// <summary>
        /// 获取指定数据库中指定类型的所有对象（跨库空格触发）。
        /// </summary>
        public List<CompletionCandidate> GetAllObjectsOfTypesForDb(string databaseName, params string[] typeCodes)
        {
            if (_currentServer == null) return new List<CompletionCandidate>();
            var cacheKey = $"{_currentServer}.{databaseName}";

            // 快速路径：缓存已就绪
            if (_cache.TryGetValue(cacheKey, out var cache))
            {
                return FilterAndFormatObjects(cache.Objects, databaseName, typeCodes, cache.TypeIndex);
            }

            // ★ 慢路径：缓存正在后台加载中，短暂轮询等待
            for (int i = 0; i < 30; i++)
            {
                System.Threading.Thread.Sleep(50);
                if (_cache.TryGetValue(cacheKey, out cache))
                {
                    _logger.Debug($"[DbCache] 轮询等待 {databaseName} 缓存就绪: 耗时约 {(i + 1) * 50}ms");
                    return FilterAndFormatObjects(cache.Objects, databaseName, typeCodes);
                }
            }

            _logger.Warning($"[DbCache] 等待超时: {databaseName} 缓存未就绪");
            return new List<CompletionCandidate>();
        }

        /// <summary>
        /// 从对象列表中按类型过滤并格式化为 CompletionCandidate。
        /// US-02: 优先使用 typeIndex O(1) 定位；US-01/03: 限量 Take(250) + 免 OrderBy。
        /// </summary>
        private static List<CompletionCandidate> FilterAndFormatObjects(
            List<DatabaseObjectInfo> objects, string databaseName, string[] typeCodes,
            Dictionary<string, List<DatabaseObjectInfo>>? typeIndex = null)
        {
            List<DatabaseObjectInfo> filtered;

            // US-02: 优先从 typeIndex 快速定位
            if (typeIndex != null && typeIndex.Count > 0)
            {
                var set = new HashSet<string>(typeCodes, StringComparer.OrdinalIgnoreCase);
                filtered = typeIndex
                    .Where(kv => set.Contains(kv.Key))
                    .SelectMany(kv => kv.Value)
                    .ToList();
            }
            else
            {
                filtered = objects
                    .Where(o => typeCodes.Contains(o.TypeCode, StringComparer.OrdinalIgnoreCase))
                    .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                    .ToList();
            }

            return filtered
                .Take(MaxCompletionCandidates)
                .Select(o => CreateObjectCandidate(o, "", databaseName))
                .ToList();
        }

        // ============================================================
        // Schema 候选生成（v1.1 新增）
        // ============================================================

        /// <summary>
        /// 获取指定数据库的 Schema 列表作为补全候选。
        /// 如果缓存未就绪，短暂轮询等待（最多 1.5 秒）。
        /// </summary>
        public List<CompletionCandidate> GetSchemaCandidatesForDb(string dbName, string prefix)
        {
            if (_currentServer == null) return new List<CompletionCandidate>();
            var cacheKey = $"{_currentServer}.{dbName}";

            // 快速路径：缓存已就绪
            if (_cache.TryGetValue(cacheKey, out var cache))
                return BuildSchemaCandidates(cache, prefix);

            // ★ 慢路径：轮询等待缓存就绪（最多 1.5 秒）
            for (int i = 0; i < 30; i++)
            {
                System.Threading.Thread.Sleep(50);
                if (_cache.TryGetValue(cacheKey, out cache))
                {
                    _logger.Debug($"[DbCache] Schema 轮询等待 {dbName} 缓存就绪: 耗时约 {(i + 1) * 50}ms");
                    return BuildSchemaCandidates(cache, prefix);
                }
            }

            _logger.Warning($"[DbCache] Schema 等待超时: {dbName} 缓存未就绪");
            return new List<CompletionCandidate>();
        }

        /// <summary>
        /// 从缓存中构建 Schema 候选列表（内部辅助方法）。
        /// </summary>
        private static List<CompletionCandidate> BuildSchemaCandidates(DatabaseObjectCache cache, string prefix)
        {
            var candidates = new List<CompletionCandidate>();
            foreach (var schema in cache.Schemas)
            {
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

        // ============================================================
        // 字段补全（v1.1 新增）
        // ============================================================

        /// <summary>
        /// 获取指定表的列信息作为补全候选，并按 prefix 过滤。
        /// </summary>
        public List<ColumnInfo>? GetTableColumns(string tableName, string prefix)
        {
            // ★ Bug 修复：去除方括号（表名可能包含 [MESDATA].dbo.[HONEY_832_MM_HEADER] 残留的方括号）
            var cleanTableName = tableName.Replace("[", "").Replace("]", "");

            // 解析表名，确定搜索的缓存 key
            string searchKey = _currentCacheKey ?? "";
            string searchName = cleanTableName;

            // 如果表名包含点号，可能是 DB.Schema.Table 或 Schema.Table 格式
            var parts = cleanTableName.Split('.');
            if (parts.Length >= 3)
            {
                // 格式: DB.Schema.Table
                string dbName = parts[0];
                string schemaName = parts[1];
                string objName = parts[2];
                if (_currentServer != null)
                {
                    searchKey = $"{_currentServer}.{dbName}";
                    searchName = $"{schemaName}.{objName}";
                }
            }
            else if (parts.Length == 2)
            {
                // 格式: Schema.Table — 在当前缓存中搜索
                searchName = cleanTableName;
            }

            _logger.Debug($"[DbCache] GetTableColumns: tableName='{tableName}', searchKey='{searchKey}', searchName='{searchName}'");

            if (_cache.TryGetValue(searchKey, out var cache))
            {
                var obj = cache.Objects
                    .Where(o => (o.TypeCode == "U" || o.TypeCode == "V") &&
                                string.Equals($"{o.Schema}.{o.Name}", searchName, StringComparison.OrdinalIgnoreCase))
                    .FirstOrDefault();

                if (obj == null)
                {
                    _logger.Debug($"[DbCache] GetTableColumns: 未找到对象 '{searchName}' in cache '{searchKey}'");
                    return null;
                }

                if (obj.Columns == null)
                {
                    _logger.Warning($"[DbCache] GetTableColumns: 对象 '{searchName}' 的 Columns 为 null（缓存可能过旧），触发后台刷新");
                    // ★ 触发后台刷新该数据库的缓存（覆盖旧数据）
                    _ = Task.Run(() => RefreshSingleDatabaseAsync(cache.Database));
                    return null;
                }

                _logger.Debug($"[DbCache] GetTableColumns: 找到 '{searchName}', {obj.Columns.Count} 列");

                if (string.IsNullOrEmpty(prefix))
                    return obj.Columns;

                var lowerPrefix = prefix.ToLowerInvariant();
                return obj.Columns
                    .Where(c => c.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                                CompletionCandidate.FuzzyMatch(prefix, c.Name) >= 0)
                    .ToList();
            }

            _logger.Debug($"[DbCache] GetTableColumns: 缓存 '{searchKey}' 不存在");
            return null;
        }

        /// <summary>
        /// 检查名称是否为当前缓存中的已知表名，返回完整表名（schema.Name）。
        /// </summary>
        public string? ResolveTableName(string name)
        {
            if (_cache.TryGetValue(_currentCacheKey ?? "", out var cache))
            {
                var match = cache.Objects.FirstOrDefault(o =>
                    (o.TypeCode == "U" || o.TypeCode == "V") &&
                    string.Equals(o.Name, name, StringComparison.OrdinalIgnoreCase));
                if (match != null)
                    return $"{match.Schema}.{match.Name}";
            }
            return null;
        }

        // ============================================================
        // 跨库+Schema 过滤（v1.1 新增）
        // ============================================================

        /// <summary>
        /// 在指定数据库+Schema 中搜索匹配前缀的对象。
        /// 如果缓存未就绪，短暂轮询等待（最多 1.5 秒）。
        /// </summary>
        public List<CompletionCandidate> GetMatchingObjectsForDbAndSchema(
            string dbName, string schema, string prefix, string category)
        {
            if (_currentServer == null) return new List<CompletionCandidate>();
            var cacheKey = $"{_currentServer}.{dbName}";

            // 快速路径：缓存已就绪
            if (_cache.TryGetValue(cacheKey, out var cache))
                return BuildSchemaFilteredCandidates(cache, schema, prefix, category);

            // ★ 慢路径：轮询等待缓存就绪（最多 1.5 秒）
            for (int i = 0; i < 30; i++)
            {
                System.Threading.Thread.Sleep(50);
                if (_cache.TryGetValue(cacheKey, out cache))
                {
                    _logger.Debug($"[DbCache] Schema对象 轮询等待 {dbName} 缓存就绪: 耗时约 {(i + 1) * 50}ms");
                    return BuildSchemaFilteredCandidates(cache, schema, prefix, category);
                }
            }

            _logger.Warning($"[DbCache] Schema对象 等待超时: {dbName} 缓存未就绪");
            return new List<CompletionCandidate>();
        }

        /// <summary>
        /// 从缓存中按 Schema+类型 过滤对象并构建候选列表（内部辅助方法）。
        /// </summary>
        private static List<CompletionCandidate> BuildSchemaFilteredCandidates(
            DatabaseObjectCache cache, string schema, string prefix, string category)
        {
            // 筛选指定 Schema 的对象
            var schemaObjects = cache.Objects
                .Where(o => string.Equals(o.Schema, schema, StringComparison.OrdinalIgnoreCase))
                .ToList();

            // 根据类别筛选类型
            var typeCodes = category switch
            {
                "ProceduresAndFunctions" => new[] { "P", "FN", "IF", "TF" },
                "TablesAndViews" => new[] { "U", "V" },
                _ => new[] { "U", "V", "P", "FN", "IF", "TF" },
            };

            var filtered = schemaObjects
                .Where(o => typeCodes.Contains(o.TypeCode))
                .ToList();

            // 按前缀过滤
            if (!string.IsNullOrEmpty(prefix))
            {
                filtered = filtered
                    .Where(o => o.Name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    .ToList();
            }

            return filtered
                .OrderBy(o => o.Name, StringComparer.OrdinalIgnoreCase)
                .Select(o => new CompletionCandidate
                {
                    DisplayText = $"{o.Schema}.{o.Name}",
                    FilterText = o.Name,
                    TypeTag = GetTypeTagName(o.TypeCode),
                    TypeCode = (o.TypeCode ?? "").Trim(),
                    InsertText = $"[{o.Schema}].[{o.Name}]",
                    ItemType = CompletionItemType.DatabaseObject,
                    Source = o,
                    MatchScore = 0,
                }).ToList();
        }

        /// <summary>
        /// 获取当前缓存的统计信息。
        /// </summary>
        public string GetCacheStatus()
        {
            if (_currentObjects == null)
                return "无缓存";

            return $"{_currentCacheKey}: {_currentObjects.Count} 个对象，共 {_cache.Count} 个数据库已缓存";
        }

        /// <summary>
        /// 检查当前连接是否有缓存。
        /// </summary>
        public bool HasCacheForCurrentConnection()
        {
            return _currentObjects != null && _currentObjects.Count > 0;
        }

        /// <summary>
        /// 获取当前服务器标识（用于诊断）。
        /// </summary>
        public string? GetCurrentServer() => _currentServer;

        /// <summary>
        /// 获取当前数据库名称。
        /// </summary>
        public string? GetCurrentDatabase() => _currentDatabase;

        /// <summary>
        /// 获取当前缓存的数据库名称列表（含服务器信息）。
        /// </summary>
        public List<string> GetCachedDatabaseKeys()
        {
            return _cache.Keys.ToList();
        }
    }

    // ================================================================
    // 数据模型
    // ================================================================

    /// <summary>
    /// 数据库对象信息。
    /// </summary>
    public class DatabaseObjectInfo
    {
        [JsonProperty("schema")]
        public string Schema { get; set; } = "";

        [JsonProperty("name")]
        public string Name { get; set; } = "";

        [JsonProperty("typeCode")]
        public string TypeCode { get; set; } = "";

        [JsonProperty("typeName")]
        public string TypeName { get; set; } = "";

        /// <summary>表的列信息列表（仅 TypeCode="U" 或 "V" 时有值）</summary>
        [JsonProperty("columns")]
        public List<ColumnInfo>? Columns { get; set; }

        /// <summary>SP/函数的参数列表（仅 TypeCode="P"/"FN"/"IF"/"TF" 时有值）</summary>
        [JsonProperty("parameters")]
        public List<ParameterInfo>? Parameters { get; set; }

        /// <summary>表的索引/约束列表（仅 TypeCode="U" 或 "V" 时有值）</summary>
        [JsonProperty("indexes")]
        public List<IndexInfo>? Indexes { get; set; }

        /// <summary>对象名称的小写形式（运行时填充，不持久化），用于快速匹配，避免查询期反复 ToLowerInvariant</summary>
        [JsonIgnore]
        public string NameLower { get; set; } = "";
    }

    /// <summary>
    /// 表列信息。
    /// </summary>
    public class ColumnInfo
    {
        [JsonProperty("name")]          public string Name { get; set; } = "";
        [JsonProperty("dataType")]      public string DataType { get; set; } = "";
        [JsonProperty("maxLength")]     public short MaxLength { get; set; }
        [JsonProperty("precision")]     public byte Precision { get; set; }
        [JsonProperty("scale")]         public byte Scale { get; set; }
        [JsonProperty("isNullable")]    public bool IsNullable { get; set; }
        [JsonProperty("isIdentity")]    public bool IsIdentity { get; set; }
        [JsonProperty("isComputed")]    public bool IsComputed { get; set; }
        [JsonProperty("computedDef")]   public string? ComputedDefinition { get; set; }
        [JsonProperty("hasDefault")]    public bool HasDefault { get; set; }
        [JsonProperty("defaultDef")]    public string? DefaultDefinition { get; set; }
        [JsonProperty("defaultValue")]  public string? DefaultValue { get; set; }
        [JsonProperty("description")]   public string? Description { get; set; }
    }

    /// <summary>
    /// SP/函数参数信息。
    /// </summary>
    public class ParameterInfo
    {
        [JsonProperty("name")]       public string Name { get; set; } = "";      // 如 "@Id"
        [JsonProperty("dataType")]   public string DataType { get; set; } = "";  // 如 "INT"
        [JsonProperty("maxLength")]  public short MaxLength { get; set; }
        [JsonProperty("isOutput")]   public bool IsOutput { get; set; }
        [JsonProperty("hasDefault")] public bool HasDefault { get; set; }
    }

    /// <summary>
    /// 索引/约束信息。
    /// </summary>
    public class IndexInfo
    {
        [JsonProperty("name")]           public string Name { get; set; } = "";
        [JsonProperty("typeDesc")]       public string TypeDesc { get; set; } = "";
        [JsonProperty("isUnique")]       public bool IsUnique { get; set; }
        [JsonProperty("isPrimaryKey")]    public bool IsPrimaryKey { get; set; }
        [JsonProperty("isUniqueConst")]  public bool IsUniqueConstraint { get; set; }
        [JsonProperty("keyColumns")]      public string KeyColumns { get; set; } = "";
        [JsonProperty("includedCols")]   public string? IncludedColumns { get; set; }
        [JsonProperty("hasFilter")]      public bool HasFilter { get; set; }
        [JsonProperty("filterDef")]      public string? FilterDefinition { get; set; }
    }

    /// <summary>
    /// 数据库对象缓存（按数据库维度）。
    /// </summary>
    public class DatabaseObjectCache
    {
        /// <summary>缓存版本号，升版本可强制使旧缓存失效（v2: 含 Parameters）</summary>
        [JsonProperty("version")]
        public int Version { get; set; } = CurrentCacheVersion;

        /// <summary>当前缓存版本常量（v4: 含 NameLower + TypeIndex + 预排序）</summary>
        public const int CurrentCacheVersion = 4;

        [JsonProperty("server")]
        public string Server { get; set; } = "";

        [JsonProperty("database")]
        public string Database { get; set; } = "";

        [JsonProperty("lastUpdated")]
        public DateTime LastUpdated { get; set; }

        [JsonProperty("objects")]
        public List<DatabaseObjectInfo> Objects { get; set; } = new();

        /// <summary>数据库中的所有 Schema 名称列表（新增 v1.1）</summary>
        [JsonProperty("schemas")]
        public List<string> Schemas { get; set; } = new();

        /// <summary>
        /// 运行时前缀索引（不持久化，从文件加载后重建）。
        /// key = 小写前缀（1~3 字符），value = 匹配该前缀的对象列表。
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, List<DatabaseObjectInfo>> PrefixIndex { get; set; } = new();

        /// <summary>
        /// 运行时类型索引（不持久化，从文件加载后重建）。
        /// key = TypeCode（U/V/P/FN/...），value = 该类型的对象列表。
        /// 使 GetAllObjectsOfTypes 从 O(n) 全量遍历降为 O(1) 定位。
        /// </summary>
        [JsonIgnore]
        public Dictionary<string, List<DatabaseObjectInfo>> TypeIndex { get; set; } = new();

        /// <summary>
        /// 重建所有运行时索引（前缀索引 + 类型索引 + NameLower + 预排序）。
        /// 从文件加载后或缓存构建后调用。
        /// </summary>
        public void RebuildPrefixIndex()
        {
            // 预排序：构建期一次性排序，查询期免 OrderBy（US-03）
            Objects.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));

            // 预计算 NameLower：避免查询期反复 ToLowerInvariant（US-05）
            foreach (var obj in Objects)
            {
                obj.NameLower = obj.Name.ToLowerInvariant();
            }

            // 构建前缀索引
            PrefixIndex = DatabaseObjectCacheService.BuildPrefixIndex(Objects);

            // 构建类型索引（US-02）
            TypeIndex = new Dictionary<string, List<DatabaseObjectInfo>>(StringComparer.OrdinalIgnoreCase);
            foreach (var obj in Objects)
            {
                var typeCode = (obj.TypeCode ?? "").Trim();
                if (!TypeIndex.TryGetValue(typeCode, out var list))
                {
                    list = new List<DatabaseObjectInfo>();
                    TypeIndex[typeCode] = list;
                }
                list.Add(obj);
            }
        }
    }

    // ================================================================
    // 补全候选项模型
    // ================================================================

    /// <summary>
    /// 补全候选项类型。
    /// </summary>
    public enum CompletionItemType
    {
        /// <summary>SQL 模板</summary>
        Template,

        /// <summary>SQL 关键词</summary>
        Keyword,

        /// <summary>数据库对象（表、视图、SP、函数）</summary>
        DatabaseObject,

        /// <summary>数据库名称</summary>
        Database,

        /// <summary>Schema 名称（v1.1 新增）</summary>
        Schema,

        /// <summary>表字段名（v1.1 新增）</summary>
        Field,
    }

    /// <summary>
    /// 统一补全候选项模型。
    /// 同时支持 SQL 模板、关键词、数据库对象、数据库名称。
    /// </summary>
    public class CompletionCandidate
    {
        /// <summary>主显示文本（如 "dbo.Users" 或 "sf" 或 "[MyDB]"）</summary>
        public string DisplayText { get; set; } = "";

        /// <summary>过滤文本（用户输入需要匹配的内容）</summary>
        public string FilterText { get; set; } = "";

        /// <summary>类型标签（如 "模板"、"表"、"视图"、"SP"、"函数"、"DB"）</summary>
        public string TypeTag { get; set; } = "";

        /// <summary>数据库对象类型代码（如 "U"、"V"、"P"、"FN"、"IF"、"TF"、"DB"），模板为空</summary>
        public string TypeCode { get; set; } = "";

        /// <summary>所属数据库名称（跨库对象有此值，当前 DB 对象为 null）</summary>
        public string? DatabaseName { get; set; }

        /// <summary>选中后插入的文本</summary>
        public string? InsertText { get; set; }

        /// <summary>预览文本（仅模板有）</summary>
        public string? PreviewText { get; set; }

        /// <summary>候选项类型</summary>
        public CompletionItemType ItemType { get; set; }

        /// <summary>原始数据（模板对象或数据库对象信息）</summary>
        public object? Source { get; set; }

        /// <summary>模糊匹配得分（越高越匹配，-1 表示不匹配）</summary>
        public int MatchScore { get; set; }

        /// <summary>
        /// 模糊匹配算法：支持精确匹配、前缀匹配、子序列匹配。
        /// 返回得分（越高越匹配），-1 表示不匹配。
        /// 
        /// 匹配规则（优先级从高到低）：
        /// 1. 精确匹配（忽略大小写）→ 1000 分
        /// 2. 前缀匹配（target 以 input 开头）→ 500 + 长度差越小越高
        /// 3. 子序列匹配（input 的所有字符按顺序出现在 target 中）→ 100 + 连续匹配奖励
        /// 4. 包含匹配（input 是 target 的子串）→ 80 分
        /// </summary>
        public static int FuzzyMatch(string input, string target)
        {
            if (string.IsNullOrEmpty(input) || string.IsNullOrEmpty(target))
                return -1;

            // 统一转小写比较
            input = input.ToLowerInvariant();
            target = target.ToLowerInvariant();

            // 1. 精确匹配
            if (target == input)
                return 1000;

            // 2. 前缀匹配
            if (target.StartsWith(input, StringComparison.Ordinal))
                return 500 - (target.Length - input.Length);

            // 3. 子序列匹配（fuzzy）：input 的所有字符按顺序出现在 target 中
            int inputIdx = 0;
            int consecutive = 0;
            int maxConsecutive = 0;
            int firstMatchPos = -1;

            for (int i = 0; i < target.Length && inputIdx < input.Length; i++)
            {
                if (target[i] == input[inputIdx])
                {
                    if (firstMatchPos < 0)
                        firstMatchPos = i;
                    consecutive++;
                    maxConsecutive = Math.Max(maxConsecutive, consecutive);
                    inputIdx++;
                }
                else
                {
                    consecutive = 0;
                }
            }

            if (inputIdx == input.Length)
            {
                int score = 100;
                score += maxConsecutive * 15;
                score -= firstMatchPos * 3;
                score -= (target.Length - input.Length);
                return Math.Max(score, 10);
            }

            // 4. 包含匹配
            if (target.Contains(input))
                return 80;

            return -1;
        }
    }
}