using System;
using System.Collections.Concurrent;
using System.Data;
using Microsoft.Data.SqlClient;

namespace SQLFlowCore.Common
{
    public static class DatabaseCache
    {
        private class CacheItem
        {
            public DataTable Table { get; set; }
            public DateTime LastRefreshTime { get; set; }
        }

        private static readonly ConcurrentDictionary<string, CacheItem> _cachedTables = new();
        private static readonly TimeSpan _refreshInterval = TimeSpan.FromHours(12);

        public static DataTable GetTable(string tableName, SqlConnection connection, Func<SqlConnection, DataTable> loadMethod)
        {
            if (!_cachedTables.TryGetValue(tableName, out var cacheItem) || DateTime.Now - cacheItem.LastRefreshTime > _refreshInterval)
            {
                lock (_cachedTables)
                {
                    if (!_cachedTables.TryGetValue(tableName, out cacheItem) || DateTime.Now - cacheItem.LastRefreshTime > _refreshInterval)
                    {
                        var table = loadMethod(connection);
                        cacheItem = new CacheItem { Table = table, LastRefreshTime = DateTime.Now };
                        _cachedTables[tableName] = cacheItem;
                    }
                }
            }
            return cacheItem.Table;
        }

        public static bool TableExists(string tableName)
        {
            return _cachedTables.ContainsKey(tableName);
        }

        public static void ClearCache()
        {
            _cachedTables.Clear();
        }

        public static void AddOrUpdateTable(string tableName, DataTable table)
        {
            var cacheItem = new CacheItem { Table = table, LastRefreshTime = DateTime.Now };
            _cachedTables[tableName] = cacheItem;
        }
    }
}
