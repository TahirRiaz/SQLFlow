using Microsoft.Data.SqlClient;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Radzen;
using SQLFlowCore.Common;

namespace SQLFlowUi.Components.Pages
{
    public partial class CalculateLineage
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
        IConfiguration Configuration { get; set; }

        [Inject]
        protected SQLFlowUi.sqlflowProdService sqlflowProdService { get; set; }

        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> sysDataSources;
        protected System.Linq.IQueryable<SQLFlowUi.Data.MetaData.DicObjectIntStr> allObj;
        
        [Parameter]
        public string all { get; set; }

        [Parameter]
        public string alias { get; set; }




        public string afBaseURL { get; set; }
      
        public string aliasDefault
        {
            get
            {
                if (string.IsNullOrEmpty(alias))
                {
                    return ""; // or any default date you prefer
                }
                else
                {
                    
                    return alias;
                }
            }
            set { }
        }

        public int allObjDefault
        {
            get
            {
                if (string.IsNullOrEmpty(all))
                {
                    return 1; // or any default date you prefer
                }
                else
                {

                    return int.Parse(all);
                }
            }
            set { }
        }

        protected override async Task OnInitializedAsync()
        {
            sysDataSources = await sqlflowProdService.GetSysDataSource();
            allObj = SQLFlowUi.Data.MetaDataInfo.GetAllObj();
            
            string connectionString = Environment.GetEnvironmentVariable("SQLFlowConStr");//@Configuration.GetConnectionString("sqlflowProdConnection");
            ConStringParser conStringParser = new ConStringParser(connectionString)
            {
                ConBuilderMsSql =
                {
                    ApplicationName = "SQLFlow App"
                }
            };
            string sqlFlowConStr = conStringParser.ConBuilderMsSql.ConnectionString;

            afBaseURL = SQLFlowApiUrl(sqlFlowConStr);
        }


        public string SQLFlowApiUrl(string connectionString)
        {
            string result = null;

            using (SqlConnection connection = new SqlConnection(connectionString))
            {
                connection.Open();

                using (SqlCommand command = new SqlCommand("SELECT [flw].[GetWebApiUrl]()", connection))
                {
                    SqlDataReader reader = command.ExecuteReader();

                    if (reader.Read())
                    {
                        result = reader.GetString(0); // assuming the result is a string
                    }
                }
            }

            return result;
        }
    }
}