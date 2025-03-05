using System.Data;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.SqlClient;
using Radzen;
using SQLFlowUi.Models.sqlflowProd;
using SQLFlowCore.Utils.Period;
using SQLFlowCore.Common;


namespace SQLFlowUi.Components.Pages
{
    public partial class BuildDimPeriod
    {
        [Inject]
        protected IJSRuntime JSRuntime { get; set; }

        [Inject]
        protected NavigationManager NavigationManager { get; set; }

        [Inject]
        protected DialogService DialogService { get; set; }

        [Inject]
        protected TooltipService TooltipService { get; set; }

        [Inject]
        protected ContextMenuService ContextMenuService { get; set; }

        [Inject]
        protected NotificationService NotificationService { get; set; }
        [Inject]
        protected SQLFlowUi.sqlflowProdService sqlflowProdService { get; set; }

        [Inject]
        IConfiguration Configuration { get; set; }

        protected System.Linq.IQueryable<SQLFlowUi.Data.MetaData.DicObjectStrStr> countryList;
        protected System.Linq.IQueryable<SQLFlowUi.Data.MetaData.DicObjectStrStr> holidayLang;
        protected System.Linq.IQueryable<SQLFlowUi.Data.MetaData.DicObjectIntStr> fiscalMonthStart;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.GetApiKey> getGoogleApiKeys;

        protected override async Task OnInitializedAsync()
        {
            getGoogleApiKeys = await sqlflowProdService.GetGetGoogleApiKeys();
            countryList = SQLFlowUi.Data.MetaDataInfo.GetCountry();
            holidayLang = SQLFlowUi.Data.MetaDataInfo.GetHolidayLang();
            fiscalMonthStart = SQLFlowUi.Data.MetaDataInfo.GetFiscalStartMonth();
        }

        protected async System.Threading.Tasks.Task Button0Click(Microsoft.AspNetCore.Components.Web.MouseEventArgs args)
        {
            // Show a loading notification
            // You might need to inject Radzen's NotificationService to use this
            NotificationService.Notify(NotificationSeverity.Info, "Running", "Building Period dimension...");

            // Fetch values from components
            var fromDateValue = (DateTime) FromDateComponent.Value;
            var toDateValue = (DateTime) ToDateComponent.Value;
            var countryValue = (CountryComponent.SelectedItem != null)? ((SQLFlowUi.Data.MetaData.DicObjectStrStr) CountryComponent.SelectedItem).Value : ""  ;
            var holidayLangValue = (HolidayLangComponent.SelectedItem != null)? ((SQLFlowUi.Data.MetaData.DicObjectStrStr) HolidayLangComponent.SelectedItem).Key: "";
            var apiKeyValue = ((GetApiKey) APIKeyComponent.SelectedItem).ApiKeyAlias;
            var fiscalMonthStart = ((SQLFlowUi.Data.MetaData.DicObjectIntStr)FiscalMonthStartComponent.SelectedItem).Key;


            string connectionString = Environment.GetEnvironmentVariable("SQLFlowConStr");//@Configuration.GetConnectionString("sqlflowProdConnection");
            ConStringParser conStringParser = new ConStringParser(connectionString)
            {
                ConBuilderMsSql =
                {
                    ApplicationName = "SQLFlow App"
                }
            };
            string sqlFlowConStr = conStringParser.ConBuilderMsSql.ConnectionString;


            DimPeriod dimPriod = new DimPeriod(sqlFlowConStr, fromDateValue, toDateValue, fiscalMonthStart, countryValue, holidayLangValue, apiKeyValue );
            System.Data.DataTable dt = dimPriod.BuildDt();

            // Execute stored procedure (Placeholder: Replace with your actual implementation)
            await BulkLoadDataTableToSqlServer(dt, sqlFlowConStr, "flw","SysPeriod");

            // Show a success notification
            NotificationService.Notify(NotificationSeverity.Success, "Execution Completed", "The execution process has completed. CheckForError the log for details.");
        }

        public async Task BulkLoadDataTableToSqlServer(System.Data.DataTable dataTable, string connectionString, string targetSchema, string targetTable)
        {
            string targetTableName = $"[{targetSchema}].[{ targetTable}]";
            try
            {
                using (SqlConnection connection = new SqlConnection(connectionString))
                {
                    await connection.OpenAsync();

                    // Truncate the target table before inserting new records
                    if (await TableExistsAsync(connectionString, targetSchema, targetTable))
                    {
                        // Truncate the target table before inserting new records
                        using (SqlCommand truncateCommand = new SqlCommand($"TRUNCATE TABLE {targetTableName}", connection))
                        {
                            await truncateCommand.ExecuteNonQueryAsync();
                        }
                    }
                    else
                    {
                        string createTableScript = DataTypeMapper.CreateTableSql(dataTable, targetTableName, "nvarchar(255)");
                        // Truncate the target table before inserting new records
                        using (SqlCommand truncateCommand = new SqlCommand(createTableScript, connection))
                        {
                            await truncateCommand.ExecuteNonQueryAsync();
                        }
                    }

                    using (SqlBulkCopy bulkCopy = new SqlBulkCopy(connection))
                    {
                        bulkCopy.DestinationTableName = targetTableName;

                        // Map columns if the source and destination column names differ
                        foreach (DataColumn col in dataTable.Columns)
                        {
                            bulkCopy.ColumnMappings.Add(col.ColumnName, col.ColumnName);
                            // If column names are different, use:
                            // bulkCopy.ColumnMappings.Add("SourceColumnName", "DestinationColumnName");
                        }

                        await bulkCopy.WriteToServerAsync(dataTable);
                    }
                }
            }
            catch (Exception ex)
            {
                // Handle the exception
                Console.WriteLine($"An error occurred: {ex.Message}");
            }
        }


        public async Task<bool> TableExistsAsync(string connectionString, string schemaName  , string targetTableName)
        {
            bool exists = false;

            const string sql = @"SELECT 1 
                         FROM INFORMATION_SCHEMA.TABLES 
                         WHERE TABLE_SCHEMA = @SchemaName 
                         AND TABLE_NAME = @TableName";

            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                using (SqlCommand cmd = new SqlCommand(sql, connection))
                {
                    cmd.Parameters.AddWithValue("@TableName", targetTableName);
                    cmd.Parameters.AddWithValue("@SchemaName", schemaName);

                    await connection.OpenAsync();
                    object result = await cmd.ExecuteScalarAsync();

                    if (result != null && Convert.ToInt32(result) == 1)
                    {
                        exists = true;
                    }
                }
            }

            return exists;
        }
    }
}