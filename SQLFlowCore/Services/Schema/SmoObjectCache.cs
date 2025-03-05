using System;
using System.Collections.Concurrent;
using System.Threading;
using Microsoft.SqlServer.Management.Common;
using Microsoft.SqlServer.Management.Smo;

namespace SQLFlowCore.Services.Schema
{
    public static class SmoObjectCache
    {
        private static readonly ConcurrentDictionary<string, Lazy<Server>> _serverCache = new();
        private static readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Lazy<Database>>> _databaseCache = new();
        private static readonly TimeSpan _idleTimeout = TimeSpan.FromMinutes(30); // Adjust the timeout as needed

        private static readonly object _serverLock = new();
        private static readonly object _databaseLock = new();

        public static Server GetServer(string connectionString)
        {
            return _serverCache.GetOrAdd(connectionString, cs =>
            {
                return new Lazy<Server>(() =>
                {
                    var smoSqlCon = new Microsoft.Data.SqlClient.SqlConnection(cs);
                    var smoSrvCon = new ServerConnection(smoSqlCon);
                    var server = new Server(smoSrvCon);

                    // Set up idle timeout for the server connection
                    var timer = new Timer(_ => RemoveServer(cs), null, _idleTimeout, Timeout.InfiniteTimeSpan);

                    return server;
                }, LazyThreadSafetyMode.ExecutionAndPublication);
            }).Value;
        }

        public static Database GetDatabase(string connectionString, string databaseName)
        {
            var databaseDict = _databaseCache.GetOrAdd(connectionString, _ => new ConcurrentDictionary<string, Lazy<Database>>());

            return databaseDict.GetOrAdd(databaseName, dbName =>
            {
                return new Lazy<Database>(() =>
                {
                    ScriptingOptions sOpt = SmoHelper.SmoScriptingOptionsBasic();
                    var server = GetServer(connectionString);

                    lock (_serverLock)
                    {
                        var database = server.Databases[dbName];
                        if (database == null)
                        {
                            throw new InvalidOperationException($"Database '{dbName}' not found.");
                        }

                        // Prefetch necessary metadata if needed
                        database.PrefetchObjects(typeof(Table), sOpt);
                        database.PrefetchObjects(typeof(View), sOpt);

                        // Set up idle timeout for the database
                        var timer = new Timer(_ => RemoveDatabase(connectionString, dbName), null, _idleTimeout, Timeout.InfiniteTimeSpan);

                        return database;
                    }
                }, LazyThreadSafetyMode.ExecutionAndPublication);
            }).Value;
        }

        public static void ClearCache()
        {
            foreach (var server in _serverCache.Values)
            {
                if (server.IsValueCreated)
                {
                    lock (_serverLock)
                    {
                        server.Value.ConnectionContext.Disconnect();
                    }
                }
            }
            _serverCache.Clear();

            foreach (var databaseDict in _databaseCache.Values)
            {
                foreach (var database in databaseDict.Values)
                {
                    if (database.IsValueCreated)
                    {
                        lock (_databaseLock)
                        {
                            database.Value.Parent.ConnectionContext.Disconnect();
                        }
                    }
                }
            }
            _databaseCache.Clear();
        }

        public static void RemoveServer(string connectionString)
        {
            if (_serverCache.TryRemove(connectionString, out var server))
            {
                if (server.IsValueCreated)
                {
                    lock (_serverLock)
                    {
                        server.Value.ConnectionContext.Disconnect();
                    }
                }
            }
        }

        public static void RemoveDatabase(string connectionString, string databaseName)
        {
            if (_databaseCache.TryGetValue(connectionString, out var databaseDict))
            {
                if (databaseDict.TryRemove(databaseName, out var database))
                {
                    if (database.IsValueCreated)
                    {
                        lock (_databaseLock)
                        {
                            database.Value.Parent.ConnectionContext.Disconnect();
                        }
                    }
                }
            }
        }
    }
}
