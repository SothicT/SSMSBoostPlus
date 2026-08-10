using System;
using System.Collections.Concurrent;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.Data.SqlClient;
using Newtonsoft.Json;
using SSMSBoostPlus.Interfaces;

namespace SSMSBoostPlus.Services;

/// <summary>
/// 缓存的数据库连接凭据信息。
/// 用于在 SSMS 窗口标题解析获取不到密码时，使用之前成功登入过的凭据。
/// </summary>
public class CachedConnection
{
    public string ServerName { get; set; } = "";
    public string UserName { get; set; } = "";
    public string Password { get; set; } = "";
    public AuthenticationType AuthType { get; set; }
    public bool? EncryptConnection { get; set; }
    public bool? TrustServerCertificate { get; set; }
    public DateTime LastUpdated { get; set; } = DateTime.Now;
}

/// <summary>
/// 缓存文件持久化模型。
/// </summary>
internal class CacheData
{
    public CachedConnection[] Entries { get; set; } = Array.Empty<CachedConnection>();
    public DateTime SavedAt { get; set; }
}

/// <summary>
/// 缓存加载进度事件参数。
/// </summary>
public class CacheLoadProgressEventArgs : EventArgs
{
    /// <summary>当前进度百分比（0-100）</summary>
    public int PercentComplete { get; set; }
    /// <summary>状态描述</summary>
    public string StatusMessage { get; set; } = "";
    /// <summary>服务器名称</summary>
    public string? ServerName { get; set; }
}

/// <summary>
/// 数据库连接信息缓存（线程安全 + 文件持久化）。
/// 以服务器名称为 Key，缓存成功登入过的凭据，
/// 每次更新时自动保存到本地文件，SSMS 重启后自动加载。
/// </summary>
public class ConnectionCache
{
    private readonly ConcurrentDictionary<string, CachedConnection> _cache = new();
    private readonly ILoggerService? _logger;
    private string? _cacheFilePath;
    private readonly object _saveLock = new();
    private bool _savePending;

    /// <summary>缓存加载完成事件（用于 UI 进度显示）</summary>
    public event EventHandler<CacheLoadProgressEventArgs>? OnLoadProgress;

    /// <summary>是否已从文件加载完成</summary>
    public bool IsLoaded { get; private set; }

    public ConnectionCache(ILoggerService? logger = null)
    {
        _logger = logger;
    }

    /// <summary>
    /// 设置缓存文件路径（必须在 LoadFromFileAsync 之前调用）。
    /// </summary>
    public void SetCacheDirectory(string directoryPath)
    {
        Directory.CreateDirectory(directoryPath);
        _cacheFilePath = Path.Combine(directoryPath, "connection_cache.json");
        _logger?.Information($"[ConnectionCache] 缓存文件路径: {_cacheFilePath}");
    }

    /// <summary>
    /// 从本地文件异步加载缓存的登入信息。
    /// 会触发 OnLoadProgress 事件供 UI 显示进度。
    /// </summary>
    public async Task LoadFromFileAsync()
    {
        if (string.IsNullOrEmpty(_cacheFilePath) || !File.Exists(_cacheFilePath))
        {
            _logger?.Information("[ConnectionCache] 无缓存文件，跳过加载");
            IsLoaded = true;
            return;
        }

        try
        {
            ReportProgress(5, "正在读取缓存文件...");

            var json = await Task.Run(() => File.ReadAllText(_cacheFilePath));

            ReportProgress(30, "正在解析缓存数据...");

            var data = JsonConvert.DeserializeObject<CacheData>(json);
            if (data?.Entries == null || data.Entries.Length == 0)
            {
                _logger?.Information("[ConnectionCache] 缓存文件为空");
                IsLoaded = true;
                ReportProgress(100, "缓存加载完成（无数据）");
                return;
            }

            int total = data.Entries.Length;
            for (int i = 0; i < total; i++)
            {
                var entry = data.Entries[i];
                if (!string.IsNullOrWhiteSpace(entry.ServerName))
                {
                    _cache[entry.ServerName] = entry;
                }

                // 每加载 20% 报告一次进度
                var progress = 30 + (int)((double)(i + 1) / total * 60);
                if (progress % 10 == 0)
                {
                    ReportProgress(progress, $"正在加载缓存 ({i + 1}/{total})...", entry.ServerName);
                }
            }

            IsLoaded = true;
            _logger?.Information($"[ConnectionCache] ✅ 从文件加载缓存完成: {total} 条记录 (文件: {_cacheFilePath})");
            ReportProgress(100, $"缓存加载完成（{total} 条记录）");
        }
        catch (Exception ex)
        {
            _logger?.Warning($"[ConnectionCache] ⚠️ 加载缓存失败: {ex.Message}（将继续使用空缓存）");
            IsLoaded = true;
            ReportProgress(100, "缓存加载失败，使用空缓存");
        }
    }

    /// <summary>
    /// 保存缓存到本地文件。
    /// 使用异步延迟写入防抖（避免频繁写入磁盘）。
    /// </summary>
    public async Task SaveToFileAsync()
    {
        if (string.IsNullOrEmpty(_cacheFilePath)) return;

        try
        {
            lock (_saveLock)
            {
                _savePending = true;
            }

            // 延迟写入防抖：等 500ms 内没有新的 Set 调用时再写入
            await Task.Delay(500);

            lock (_saveLock)
            {
                if (!_savePending) return;
                _savePending = false;
            }

            var data = new CacheData
            {
                Entries = _cache.Values.OrderBy(e => e.ServerName).ToArray(),
                SavedAt = DateTime.Now
            };

            var json = JsonConvert.SerializeObject(data, Formatting.Indented);
            await Task.Run(() => File.WriteAllText(_cacheFilePath, json));

            _logger?.Debug($"[ConnectionCache] 💾 缓存已保存: {data.Entries.Length} 条记录");
        }
        catch (Exception ex)
        {
            _logger?.Warning($"[ConnectionCache] ⚠️ 保存缓存失败: {ex.Message}");
        }
    }

    public CachedConnection? Get(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName)) return null;

        if (_cache.TryGetValue(serverName, out var cached))
        {
            _logger?.Debug($"[ConnectionCache] ✅ Cache hit for server: {serverName}");
            return cached;
        }

        _logger?.Debug($"[ConnectionCache] ❌ 缓存未命中，服务器: {serverName}");
        return null;
    }

    public void Set(string serverName, CachedConnection connection)
    {
        if (string.IsNullOrWhiteSpace(serverName)) return;

        connection.LastUpdated = DateTime.Now;
        _cache[serverName] = connection;
        _logger?.Information($"[ConnectionCache] ✅ Cached credentials for server: {serverName} (user={connection.UserName})");

        // 触发异步保存（防抖）
        lock (_saveLock) { _savePending = true; }
        _ = SaveToFileAsync();
    }

    public void Remove(string serverName)
    {
        if (string.IsNullOrWhiteSpace(serverName)) return;

        _cache.TryRemove(serverName, out _);
        _logger?.Information($"[ConnectionCache] 🗑 已移除过期凭据，服务器: {serverName}");

        lock (_saveLock) { _savePending = true; }
        _ = SaveToFileAsync();
    }

    public bool TestConnection(CachedConnection cached, string databaseName, int timeoutSeconds = 5)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = cached.ServerName,
                InitialCatalog = databaseName,
                ConnectTimeout = timeoutSeconds,
                TrustServerCertificate = true
            };

            if (cached.AuthType == AuthenticationType.Windows)
            {
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.IntegratedSecurity = false;
                builder.UserID = cached.UserName;
                builder.Password = cached.Password;
            }

            using var conn = new SqlConnection(builder.ConnectionString);
            conn.Open();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning($"[ConnectionCache] ❌ Cached credentials expired for {cached.ServerName}: {ex.Message}");
            return false;
        }
    }

    public async Task<bool> TestConnectionAsync(CachedConnection cached, string databaseName, int timeoutSeconds = 5)
    {
        try
        {
            var builder = new SqlConnectionStringBuilder
            {
                DataSource = cached.ServerName,
                InitialCatalog = databaseName,
                ConnectTimeout = timeoutSeconds,
                TrustServerCertificate = true
            };

            if (cached.AuthType == AuthenticationType.Windows)
            {
                builder.IntegratedSecurity = true;
            }
            else
            {
                builder.IntegratedSecurity = false;
                builder.UserID = cached.UserName;
                builder.Password = cached.Password;
            }

            using var conn = new SqlConnection(builder.ConnectionString);
            await conn.OpenAsync();
            return true;
        }
        catch (Exception ex)
        {
            _logger?.Warning($"[ConnectionCache] ❌ Cached credentials expired for {cached.ServerName}: {ex.Message}");
            return false;
        }
    }

    public static CachedConnection? ExtractFromConnectionInfo(ConnectionInfo connInfo)
    {
        if (connInfo == null || string.IsNullOrEmpty(connInfo.ConnectionString))
            return null;

        try
        {
            var builder = new SqlConnectionStringBuilder(connInfo.ConnectionString);
            return new CachedConnection
            {
                ServerName = connInfo.ServerInstance ?? builder.DataSource ?? "",
                UserName = connInfo.UserName ?? builder.UserID ?? "",
                Password = builder.Password ?? "",
                AuthType = connInfo.AuthenticationType,
                TrustServerCertificate = builder.TrustServerCertificate,
                EncryptConnection = builder.Encrypt
            };
        }
        catch
        {
            return null;
        }
    }

    public static CachedConnection? ExtractFromConnectionString(string connectionString)
    {
        if (string.IsNullOrWhiteSpace(connectionString))
            return null;

        try
        {
            var builder = new SqlConnectionStringBuilder(connectionString);
            return new CachedConnection
            {
                ServerName = builder.DataSource ?? "",
                UserName = builder.UserID ?? "",
                Password = builder.Password ?? "",
                AuthType = builder.IntegratedSecurity ? AuthenticationType.Windows : AuthenticationType.SqlServer,
                TrustServerCertificate = builder.TrustServerCertificate,
                EncryptConnection = builder.Encrypt
            };
        }
        catch
        {
            return null;
        }
    }

    public string[] GetCachedServers() => _cache.Keys.ToArray();
    public int Count => _cache.Count;

    private void ReportProgress(int percent, string message, string? serverName = null)
    {
        OnLoadProgress?.Invoke(this, new CacheLoadProgressEventArgs
        {
            PercentComplete = percent,
            StatusMessage = message,
            ServerName = serverName
        });
        _logger?.Debug($"[ConnectionCache] 加载进度: {percent}% - {message}");
    }
}