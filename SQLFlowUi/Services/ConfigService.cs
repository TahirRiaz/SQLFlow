using Microsoft.Data.SqlClient;
using SQLFlowCore.Common;

namespace SQLFlowUi.Service
{
    // Include any additional namespaces required by your DialogService

    public class ConfigSettings
    {
        // Mail Server
        public string Host { get; set; }
        public int Port { get; set; }
        public bool Ssl { get; set; }
        public string User { get; set; }
        public string Password { get; set; }

        // Token
        public string SecretKey { get; set; }
        public string Issuer { get; set; }
        public string Audience { get; set; }
        public int ExpireMinutes { get; set; }

        // Jwt Auth User
        public string JwtAuthUrl { get; set; }
        public string JwtAuthUserName { get; set; }
        public string JwtAuthUserPwd { get; set; }

        public string WebApiUrl { get; set; }
        public string Login { get; set; }
        public string Logout { get; set; }
        public string CheckAuth { get; set; }

        public string ValidateToken { get; set; }
        public string CancelProcess { get; set; }

        public string Assertion { get; set; }
        public string HealthCheck { get; set; }
        public string SourceControl { get; set; }
        public string LineageMap { get; set; }
        public string FlowProcess { get; set; }
        public string FlowNode { get; set; }
        public string FlowBatch { get; set; }
        public string TrgTblSchema { get; set; }

        public string DetectUniqueKey { get; set; }

    }

    public class ConfigService
    {
        internal ConfigSettings configSettings;
        private string databaseName;
        private string dataSource;

        // Constructor that takes DialogService as a dependency
        public ConfigService()
        {
            GetConfig();
        }

        public string GetCurrentDbName()
        {
            return databaseName;
        }

        public string GetCurrentServer()
        {
            return dataSource;
        }

        private void GetConfig()
        {
            configSettings = new ConfigSettings();
            string ConStr = Environment.GetEnvironmentVariable("SQLFlowConStr");

            ConStringParser conStringParser = new ConStringParser(ConStr)
            {
                ConBuilderMsSql =
                {
                    ApplicationName = "SQLFlow App"
                }
            };

            string sqlFlowConStr = conStringParser.ConBuilderMsSql.ConnectionString;

            databaseName =  conStringParser.ConBuilderMsSql.InitialCatalog;
            dataSource = conStringParser.ConBuilderMsSql.DataSource;
            
            using (SqlConnection connection = new SqlConnection(sqlFlowConStr))
            {
                string query = @"SELECT [SecretKey], [Issuer], [Audience], [ExpireMinutes] FROM [flw].[SysJwtSettings] ";

                SqlCommand command = new SqlCommand(query, connection);
                connection.Open();

                using (SqlDataReader reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        configSettings.SecretKey = reader["SecretKey"].ToString();
                        configSettings.Issuer = reader["Issuer"].ToString();
                        configSettings.Audience = reader["Audience"].ToString();
                        configSettings.ExpireMinutes = reader["ExpireMinutes"] != DBNull.Value
                            ? Convert.ToInt32(reader["ExpireMinutes"])
                            : 0;
                    }
                }

                query = @"SELECT [Host],[Port],[Ssl],[User],[Password] FROM [flw].[SysSmtpServer] ";

                command = new SqlCommand(query, connection);
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        configSettings.Host = reader["Host"].ToString();
                        configSettings.Port = reader["Port"] != DBNull.Value ? Convert.ToInt32(reader["Port"]) : 0;
                        configSettings.Ssl = reader["Ssl"] != DBNull.Value && Convert.ToBoolean(reader["Ssl"]);
                        configSettings.User = reader["User"].ToString();
                        configSettings.Password = reader["Password"].ToString();
                    }
                }

                query = @"SELECT [JwtAuthUserName], [JwtAuthUserPwd], [JwtAuthUrl] FROM [flw].[SysJwtAuthUser] ";

                command = new SqlCommand(query, connection);
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        configSettings.JwtAuthUrl = reader["JwtAuthUrl"].ToString();
                        configSettings.JwtAuthUserName = reader["JwtAuthUserName"].ToString();
                        configSettings.JwtAuthUserPwd = reader["JwtAuthUserPwd"].ToString();
                    }
                }


                query = @"SELECT [WebApiUrl]
                              ,[Login]
                              ,[Logout]
                              ,[CheckAuth]
                              ,ValidateToken
                              ,CancelProcess
                              ,[Assertion]
                              ,[HealthCheck]
                              ,[SourceControl]
                              ,[LineageMap]
                              ,[FlowProcess]
                              ,[FlowNode]
                              ,[FlowBatch]
                              ,TrgTblSchema
                              ,DetectUniqueKey
                          FROM [flw].[SysWebApi]
                        ";

                command = new SqlCommand(query, connection);
                using (SqlDataReader reader = command.ExecuteReader())
                {
                    if (reader.Read())
                    {
                        configSettings.WebApiUrl = reader["WebApiUrl"].ToString();
                        configSettings.Login = reader["Login"].ToString();
                        configSettings.Logout = reader["Logout"].ToString();
                        configSettings.CheckAuth = reader["CheckAuth"].ToString();
                        configSettings.ValidateToken = reader["ValidateToken"].ToString();
                        configSettings.CancelProcess = reader["CancelProcess"].ToString();
                        configSettings.Assertion = reader["Assertion"].ToString();
                        configSettings.HealthCheck = reader["HealthCheck"].ToString();
                        configSettings.SourceControl = reader["SourceControl"].ToString();
                        configSettings.LineageMap = reader["LineageMap"].ToString();
                        configSettings.FlowProcess = reader["FlowProcess"].ToString();
                        configSettings.FlowNode = reader["FlowNode"].ToString();
                        configSettings.FlowBatch = reader["FlowBatch"].ToString();
                        configSettings.TrgTblSchema = reader["TrgTblSchema"].ToString();
                        configSettings.DetectUniqueKey = reader["DetectUniqueKey"].ToString();

                    }
                }



            }
        }



    }

}

