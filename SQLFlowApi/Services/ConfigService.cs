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

        public int ExpireYears4LongLived { get; set; }


        public int RefreshLineageAfterMinutes { get; set; }
        public int MaxParallelTasks { get; set; }
        public int MaxParallelSteps { get; set; }
    }

    public class ConfigService
    {
        public ConfigSettings configSettings;

        // Constructor that takes DialogService as a dependency
        public ConfigService()
        {
            GetConfig();
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
            
            using (SqlConnection connection = new SqlConnection(sqlFlowConStr))
            {

                string query = @"SELECT  [refreshLineageAfterMinutes] ,[maxParallelTasks] ,[maxParallelSteps] FROM [flw].[SysEventTaskSettings] ";

                SqlCommand command = new SqlCommand(query, connection);
                try
                {
                    connection.Open();
                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            configSettings.RefreshLineageAfterMinutes = reader["refreshLineageAfterMinutes"] != DBNull.Value
                                ? Convert.ToInt32(reader["refreshLineageAfterMinutes"])
                                : 120;
                            configSettings.MaxParallelTasks = reader["maxParallelTasks"] != DBNull.Value
                                ? Convert.ToInt32(reader["maxParallelTasks"])
                                : 2;
                            configSettings.MaxParallelSteps = reader["maxParallelSteps"] != DBNull.Value
                                ? Convert.ToInt32(reader["maxParallelSteps"])
                                : 2;
                        }
                    }


                    query = @"SELECT  [SecretKey] ,[Issuer],[Audience],[ExpireMinutes], ExpireYears4LongLived FROM [flw].[SysJwtSettings] ";
                    command = new SqlCommand(query, connection);

                    using (SqlDataReader reader = command.ExecuteReader())
                    {
                        if (reader.Read())
                        {
                            configSettings.SecretKey = reader["SecretKey"].ToString();
                            configSettings.Issuer = reader["Issuer"].ToString();
                            configSettings.Audience = reader["Audience"].ToString();
                            configSettings.ExpireMinutes = reader["ExpireMinutes"] != DBNull.Value
                                ? Convert.ToInt32(reader["ExpireMinutes"])
                                : 120;

                            configSettings.ExpireYears4LongLived = reader["ExpireYears4LongLived"] != DBNull.Value
                                ? Convert.ToInt32(reader["ExpireYears4LongLived"])
                                : 2;
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
                }
                catch (Exception e)
                {
                    Console.WriteLine(e);
                    throw;
                }
               

                
            }
            
        }
    }
        

}
