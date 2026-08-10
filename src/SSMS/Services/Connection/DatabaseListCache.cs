using System;
using System.Collections.Generic;

namespace SSMSBoostPlus.SSMS.Services.Connection;

/// <summary>
/// 数据库列表静态缓存，按 (DataSource, AuthType) 作为缓存 key。
/// 多个版本历史页签共享同一缓存，避免重复 sys.databases 查询。
/// </summary>
public static class DatabaseListCache
{
    private static readonly Dictionary<string, CachedDatabaseList> _cache = new(StringComparer.OrdinalIgnoreCase);
    private static readonly object _lock = new();

    /// <summary>
    /// 获取缓存的数据库列表。
    /// </summary>
    /// <param name="dataSource">服务器地址</param>
    /// <param name="authType">认证类型</param>
    /// <param name="cacheMinutes">缓存有效期（分钟）</param>
    /// <returns>缓存命中且未过期时返回数据库列表，否则返回 null</returns>
    public static List<string>? Get(string dataSource, string authType, int cacheMinutes)
    {
        if (cacheMinutes <= 0) return null;

        var key = BuildKey(dataSource, authType);
        lock (_lock)
        {
            if (_cache.TryGetValue(key, out var cached))
            {
                if ((DateTime.UtcNow - cached.CachedAt).TotalMinutes < cacheMinutes)
                {
                    return cached.Databases;
                }
                // 缓存过期，移除
                _cache.Remove(key);
            }
        }
        return null;
    }

    /// <summary>
    /// 写入缓存。
    /// </summary>
    public static void Set(string dataSource, string authType, List<string> databases)
    {
        var key = BuildKey(dataSource, authType);
        lock (_lock)
        {
            _cache[key] = new CachedDatabaseList
            {
                Databases = databases,
                CachedAt = DateTime.UtcNow
            };
        }
    }

    /// <summary>
    /// 清除所有缓存。
    /// </summary>
    public static void Clear()
    {
        lock (_lock)
        {
            _cache.Clear();
        }
    }

    private static string BuildKey(string dataSource, string authType)
    {
        return $"{dataSource}|{authType}";
    }

    private class CachedDatabaseList
    {
        public List<string> Databases { get; set; } = new();
        public DateTime CachedAt { get; set; }
    }
}