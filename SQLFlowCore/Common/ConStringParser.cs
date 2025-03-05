using System;
using System.Collections.Generic;
using Microsoft.Data.SqlClient;
using System.Linq;
using MySql.Data.MySqlClient;

namespace SQLFlowCore.Common
{
    public class ConStringParser
    {
        private static readonly Dictionary<string, Action<SqlConnectionStringBuilder, string>> ConnectionStringOptionsMsSql
            = new(StringComparer.OrdinalIgnoreCase)
        {
            { "server", ParseServerAddress },
            { "host", (builder, value) => builder.DataSource = value },
            { "data source", (builder, value) => builder.DataSource = value },
            { "datasource", (builder, value) => builder.DataSource = value },
            { "address", (builder, value) => builder.DataSource = value },
            { "addr", (builder, value) => builder.DataSource = value },
            { "network address", (builder, value) => builder.DataSource = value },
            { "database", (builder, value) => builder.InitialCatalog = value },
            { "initial catalog", (builder, value) => builder.InitialCatalog = value },
            { "user id", (builder, value) => builder.UserID = value },
            { "uid", (builder, value) => builder.UserID = value },
            { "username", (builder, value) => builder.UserID = value },
            { "user name", (builder, value) => builder.UserID = value },
            { "user", (builder, value) => builder.UserID = value },
            { "password", (builder, value) => builder.Password = value },
            { "pwd", (builder, value) => builder.Password = value },
            { "Application Name", (builder, value) => builder.ApplicationName = value },
            { "Connect Timeout", (builder, value) => builder.ConnectTimeout = int.TryParse(value, out var result) ? result : 0 },
            { "Connection Timeout", (builder, value) => builder.ConnectTimeout = int.TryParse(value, out var result) ? result : 0 },
            { "Encrypt", (builder, value) => builder.Encrypt = bool.TryParse(value, out var result) && result },
            { "Enlist", (builder, value) => builder.Enlist = bool.TryParse(value, out var result) && result },
            { "MultipleActiveResultSets", (builder, value) => builder.MultipleActiveResultSets = bool.TryParse(value, out var result) && result },
            { "Integrated Security", (builder, value) => builder.IntegratedSecurity = bool.TryParse(value, out var result) && result },
            { "Trusted_Connection", (builder, value) => builder.IntegratedSecurity = bool.TryParse(value, out var result) && result },
            { "Max Pool Size", (builder, value) => builder.MaxPoolSize = int.TryParse(value, out var result) ? result : 0 },
            { "Min Pool Size", (builder, value) => builder.MinPoolSize = int.TryParse(value, out var result) ? result : 0 },
            { "Packet Size", (builder, value) => builder.PacketSize = int.TryParse(value, out var result) ? result : 0 },
            { "Persist Security Info", (builder, value) => builder.PersistSecurityInfo = bool.TryParse(value, out var result) && result },
            { "Pooling", (builder, value) => builder.Pooling = bool.TryParse(value, out var result) && result },
            { "Transaction Binding", (builder, value) => builder.TransactionBinding = value },
            { "TrustServerCertificate", (builder, value) => builder.TrustServerCertificate = true }, //bool.TryParse(value, out var result) && result }
            { "Workstation ID", (builder, value) => builder.WorkstationID = value },
            { "ApplicationIntent", (builder, value) => builder.ApplicationIntent = Enum.TryParse(value, out ApplicationIntent result) ? result : ApplicationIntent.ReadWrite },
            { "Current Language", (builder, value) => builder.CurrentLanguage = value },
            { "Failover Partner", (builder, value) => builder.FailoverPartner = value },
            { "Load Balance Timeout", (builder, value) => builder.LoadBalanceTimeout = int.TryParse(value, out var result) ? result : 0 },
            { "MultiSubnetFailover", (builder, value) => builder.MultiSubnetFailover = bool.TryParse(value, out var result) && result },
            //{ "TransparentNetworkIPResolution", (builder, value) => builder.TransparentNetworkIPResolution = bool.TryParse(value, out var result) && result },
        };

        private static readonly Dictionary<string, Action<MySqlConnectionStringBuilder, string>> ConnectionStringOptionsMySql = new(StringComparer.OrdinalIgnoreCase)
        {
            { "server", (builder, value) => builder.Server = value },
            { "host", (builder, value) => builder.Server = value },
            { "port", (builder, value) => builder.Port = uint.TryParse(value, out var p) ? p : 3306 },
            { "database", (builder, value) => builder.Database = value },
            { "uid", (builder, value) => builder.UserID = value },
            { "user id", (builder, value) => builder.UserID = value },
            { "pwd", (builder, value) => builder.Password = value },
            { "password", (builder, value) => builder.Password = value },
            { "sslmode", (builder, value) => builder.SslMode = Enum.TryParse(value, true, out MySqlSslMode mode) ? mode : MySqlSslMode.Preferred },
            { "ssl-ca", (builder, value) => builder.SslCa = value },
            { "certfile", (builder, value) => builder.SslCa = value },
            { "certificatefile", (builder, value) => builder.SslCa = value },
            { "ssl-cert", (builder, value) => builder.SslCert = value },
            { "ssl-key", (builder, value) => builder.SslKey = value },
            { "certificatepassword", (builder, value) => builder.CertificatePassword = value },
            { "CertificateStoreLocation", (builder, value) => builder.CertificateStoreLocation = MySqlCertificateStoreLocation.CurrentUser },
            { "CertificateThumbprint", (builder, value) => builder.CertificateThumbprint = value },
            
            // Add other options as needed
        };

        public SqlConnectionStringBuilder ConBuilderMsSql { get; }

        public MySqlConnectionStringBuilder ConBuilderMySql { get; private set; }


        public ConStringParser(string conString)
        {
            ConBuilderMsSql = new SqlConnectionStringBuilder();
            ParseConnectionString(conString, ConnectionStringOptionsMsSql, ConBuilderMsSql);

            ConBuilderMySql = new MySqlConnectionStringBuilder();
            ParseConnectionString(conString, ConnectionStringOptionsMySql, ConBuilderMySql);
        }

        private static void ParseServerAddress(SqlConnectionStringBuilder builder, string value)
        {
            var parts = value.Split(new[] { ':' }, 3);
            if (parts.Length >= 2 && parts[0].Equals("tcp", StringComparison.OrdinalIgnoreCase))
            {
                builder.DataSource = parts[1];
                if (parts.Length == 3 && int.TryParse(parts[2], out var port))
                {
                    builder.DataSource += "," + port;
                }
            }
            else
            {
                builder.DataSource = value;
            }
        }
        /*
        private static void ParseServerAddress(SqlConnectionStringBuilder builder, string value)
        {
            // This assumes the server address may start with "tcp:" and can include a port number
            var parts = value.Split(new[] { ':' }, 2);
            if (parts.Length == 2 && parts[0].Equals("tcp", StringComparison.OrdinalIgnoreCase))
            {
                builder.DataSource = parts[1];
            }
            else
            {
                builder.DataSource = value;
            }
        }*/

        private void ParseConnectionString<T>(string conString, Dictionary<string, Action<T, string>> options, T builder)
        {
            var keyValuePairs = conString.Split(';')
                .Where(kvp => kvp.Contains('='))
                .Select(kvp => kvp.Split(new char[] { '=' }, 2))
                .ToDictionary(kvp => kvp[0].Trim(), kvp => kvp[1].Trim(), StringComparer.InvariantCultureIgnoreCase);

            foreach (var kvp in keyValuePairs)
            {
                if (options.TryGetValue(kvp.Key, out var action))
                {
                    action(builder, kvp.Value);
                }
                else
                {
                    // Debugging: Log or print the key that was not found
                    // Console.WriteLine($"Key not found: {kvp.Key}");
                }
            }
        }
    }
}
