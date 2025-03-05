using System.Data;
using System.Globalization;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.Data.SqlClient;
using Radzen;
using SQLFlowUi.Models.MetaData;
using SQLFlowCore.Common;



namespace SQLFlowUi.Components.Pages
{
    public partial class SetFileDate
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

        [Parameter]
        public int FlowID { get; set; }

        [Parameter]
        public string FileDate { get; set; }

        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.FlowDS> flowDS;
        protected System.Linq.IQueryable<DicObjectStrStr> uniqueBatches;
        
        protected override async Task OnInitializedAsync()
        {
            flowDS = await sqlflowProdService.GetFlowDs();
            // using LINQ to select distinct values based on the Batch property
            Dictionary<string, string> uniqueBatchesDic = new Dictionary<string, string>();
            foreach (var item in flowDS.Select(x => x.Batch).Distinct())
            {
                if (item != null)
                {
                    uniqueBatchesDic.Add(item, item);
                }
            }

            IQueryable<DicObjectStrStr> batchQueryable = uniqueBatchesDic
                .Select(kv => new DicObjectStrStr { Key = kv.Key, Value = kv.Value })
                .AsQueryable();

            uniqueBatches = batchQueryable;


            if (string.IsNullOrEmpty(FileDate) == false)
            {
                DateTime tmpDate = ConvertFromCustomFormat(FileDate);
                fromDate = tmpDate;
            }

            if (string.IsNullOrEmpty(FlowID.ToString()) == false)
            {
                flowId = FlowID;
            }
            


        }


        public static string ConvertToSQLFlowFormat(DateTime dateTime)
        {
            //20231001061944
            DateTime fixedDateTime = new DateTime(dateTime.Year, dateTime.Month, dateTime.Day, 0, 0, 1);
            return fixedDateTime.ToString("yyyyMMddHHmmss");
        }

        public static DateTime ConvertFromCustomFormat(string dateTimeString)
        {
            //20240117023243
            //yyyyMMddHHmmss
            DateTime dateTime;
            if (DateTime.TryParseExact(dateTimeString, "yyyyMMddHHmmss", CultureInfo.InvariantCulture, DateTimeStyles.None, out dateTime))
            {
                return dateTime;
            }
            else 
            {
                return DateTime.Now;
            }
        }


        protected async System.Threading.Tasks.Task Button0Click(Microsoft.AspNetCore.Components.Web.MouseEventArgs args)
        {
            // Show a loading notification
            //NotificationService.Notify(NotificationSeverity.Info, "Executing", "Preparing for file re-process...");

            
            //var _flowID = (flowIDComponent.SelectedItem != null)? ((FlowDS)flowIDComponent.SelectedItem).FlowID : 0;

            //var _batch = (batchComponent.SelectedItems != null) ? ((DicObjectStrStr)batchComponent.SelectedItem).Key : "";

            string connectionString = Environment.GetEnvironmentVariable("SQLFlowConStr");//@Configuration.GetConnectionString("sqlflowProdConnection");
            ConStringParser conStringParser = new ConStringParser(connectionString)
            {
                ConBuilderMsSql =
                {
                    ApplicationName = "SQLFlow App"
                }
            };
            string sqlFlowConStr = conStringParser.ConBuilderMsSql.ConnectionString;

            using (SqlConnection connection = new SqlConnection(sqlFlowConStr))
            {
                using (SqlCommand cmd = new SqlCommand("[flw].[SetFileDate]", connection))
                {
                    cmd.CommandType = CommandType.StoredProcedure;

                    cmd.Parameters.Add("@FlowID", SqlDbType.Int).Value = flowId;
                    cmd.Parameters.Add("@Batch", SqlDbType.VarChar).Value = batchlist;
                    cmd.Parameters.Add("@FileDate", SqlDbType.VarChar).Value = ConvertToSQLFlowFormat(fromDate);

                    await connection.OpenAsync();

                    cmd.ExecuteNonQuery();

                    cmd.Dispose();
                    connection.Close();
                    connection.Dispose();
                }
            }

            // Show a success notification
            NotificationService.Notify(NotificationSeverity.Success, "Executed", "You can now reprocess the files by executing the data flow or node");
        }



        protected async System.Threading.Tasks.Task Button1Click(Microsoft.AspNetCore.Components.Web.MouseEventArgs args)
        {
            // Show a loading notification
            //NotificationService.Notify(NotificationSeverity.Info, "Executing", "Restoring Filedate from SysLog...");

            //var _flowID = (flowIDComponent.SelectedItem != null) ? ((FlowDS)flowIDComponent.SelectedItem).FlowID : 0;

            string connectionString = Environment.GetEnvironmentVariable("SQLFlowConStr");//@Configuration.GetConnectionString("sqlflowProdConnection");
            ConStringParser conStringParser = new ConStringParser(connectionString)
            {
                ConBuilderMsSql =
                {
                    ApplicationName = "SQLFlow App"
                }
            };
            string sqlFlowConStr = conStringParser.ConBuilderMsSql.ConnectionString;

            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                using (SqlCommand cmd = new SqlCommand("[flw].[SetFileDate]", connection))
                {
                    cmd.CommandType = CommandType.StoredProcedure;

                    cmd.Parameters.Add("@FlowID", SqlDbType.Int).Value = flowId;
                    cmd.Parameters.Add("@Batch", SqlDbType.VarChar).Value = batchlist;
                    cmd.Parameters.Add("@FileDate", SqlDbType.VarChar).Value = -1;

                    await connection.OpenAsync();

                    cmd.ExecuteNonQuery();

                    cmd.Dispose();
                    connection.Close();
                    connection.Dispose();
                }
            }
            // Show a success notification
            NotificationService.Notify(NotificationSeverity.Success, "Executed", "FileDate restored. Scheduled executions will resume normally.");
        }


        protected async System.Threading.Tasks.Task Button2Click(Microsoft.AspNetCore.Components.Web.MouseEventArgs args)
        {
            // Show a loading notification
            //NotificationService.Notify(NotificationSeverity.Info, "Executing", "Restoring Filedate from SysLog...");

            //var _flowID = (flowIDComponent.SelectedItem != null) ? ((FlowDS)flowIDComponent.SelectedItem).FlowID : 0;
            string connectionString = Environment.GetEnvironmentVariable("SQLFlowConStr");//@Configuration.GetConnectionString("sqlflowProdConnection");
            ConStringParser conStringParser = new ConStringParser(connectionString)
            {
                ConBuilderMsSql =
                {
                    ApplicationName = "SQLFlow App"
                }
            };
            string sqlFlowConStr = conStringParser.ConBuilderMsSql.ConnectionString;

            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                using (SqlCommand cmd = new SqlCommand("[flw].[SetFileDate]", connection))
                {
                    cmd.CommandType = CommandType.StoredProcedure;

                    cmd.Parameters.Add("@FlowID", SqlDbType.Int).Value = 0;
                    cmd.Parameters.Add("@Batch", SqlDbType.VarChar).Value = "";
                    cmd.Parameters.Add("@FileDate", SqlDbType.VarChar).Value = -1;

                    await connection.OpenAsync();

                    cmd.ExecuteNonQuery();

                    cmd.Dispose();
                    connection.Close();
                    connection.Dispose();
                }
            }
            // Show a success notification
            NotificationService.Notify(NotificationSeverity.Success, "Executed", "FetchMinValue From Source is set to false on ingestion flows");
        }




    }
}