using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;
using Newtonsoft.Json;
using SQLFlowUi.Controllers;
using SQLFlowUi.Models.sqlflowProd;

namespace SQLFlowUi.Components.Pages
{
    public partial class ReportFlowHealthCheckX
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
        public sqlflowProdService dwSqlflowProdService { get; set; }

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.FlowHealthCheck> reportFlowHealthCheck1;

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.FlowHealthCheck> currentGroup;

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.ReportAssertion> reportAssertion;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.FlowHealthCheck> grid0;
        protected IEnumerable<IGrouping<int, SQLFlowUi.Models.sqlflowProd.FlowHealthCheck>> groupedData;

        protected HealthCheckHeader header;
        protected string pgTitle = "";
        protected string search = "";  

        protected int noOfRows = 0;

        protected int flowId = 638;
        
        protected override async Task OnInitializedAsync()
        {

            reportAssertion = await dwSqlflowProdService.GetReportAssertion(new Query { Filter = $@"i => i.FlowID  = (@0)", FilterParameters = new object[] { flowId } });

            reportFlowHealthCheck1 = await dwSqlflowProdService.GetReportFlowHealthCheck(new Query { Filter = $@"i => i.FlowID  = (@0)", FilterParameters = new object[] { flowId } });
            reportFlowHealthCheck1 =  reportFlowHealthCheck1.Where(flowHealthCheck => flowHealthCheck.AnomalyDetected == true);

            groupedData = GetHealthChecksGroupedByHealthCheckID(reportFlowHealthCheck1);
            header = GetSingleMLModelSelectionFromGroupedData(groupedData);

            pgTitle = $"Health check: {header.trgObject}";
            
            currentGroup = groupedData.ElementAt(0);
            noOfRows = reportFlowHealthCheck1.Count();

        }
        
        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                //var chartOptions = HealthCheckJson.CreateChartOptions(reportFlowHealthCheck1);
                //var json = JsonConvert.SerializeObject(chartOptions);
                //string jsCode = GenerateJavaScriptCode(json);
                //await JSRuntime.InvokeVoidAsync("eval", jsCode);
            }
        }

       


        public string ProcessAssertion(SQLFlowUi.Models.sqlflowProd.ReportAssertion assertion)
        {
            if (assertion == null)
            {
                throw new ArgumentNullException(nameof(assertion), "Assertion cannot be null.");
            }

            // Assuming 'Status' is a property of ReportAssertion. Replace with actual property.
            switch (assertion.Result)
            {
                case "1":
                    return "done";
                case "0":
                    return "dangerous";
                default:
                    return "info";
            }
        }

        public ButtonStyle ProcessAssertionColor(SQLFlowUi.Models.sqlflowProd.ReportAssertion assertion)
        {
            if (assertion == null)
            {
                throw new ArgumentNullException(nameof(assertion), "Assertion cannot be null.");
            }

            // Assuming 'Status' is a property of ReportAssertion. Replace with actual property.
            switch (assertion.Result)
            {
                case "1":
                    return ButtonStyle.Success;
                case "0":
                    return ButtonStyle.Danger;
                default:
                    return ButtonStyle.Info;
            }
        }



        public IEnumerable<IGrouping<int, SQLFlowUi.Models.sqlflowProd.FlowHealthCheck>> GetHealthChecksGroupedByHealthCheckID(IEnumerable<SQLFlowUi.Models.sqlflowProd.FlowHealthCheck> baseData)
        {
            // Create a materialized copy of the data first
            var materializedData = baseData.Select(x => new FlowHealthCheck
            {
                HealthCheckID = x.HealthCheckID,
                BaseValue = x.BaseValue,
                Date = x.Date,
                PredictedValue = x.PredictedValue,
                AnomalyDetected = x.AnomalyDetected,
                MLModelSelection = x.MLModelSelection,
                MLModelName = x.MLModelName,
                MLModelDate = x.MLModelDate,
                trgObject = x.trgObject,
                HealthCheckName = x.HealthCheckName
            }).ToList();

            // Now group the materialized data
            return materializedData.GroupBy(x => x.HealthCheckID);
        }


        public HealthCheckHeader GetSingleMLModelSelectionFromGroupedData(IEnumerable<IGrouping<int, SQLFlowUi.Models.sqlflowProd.FlowHealthCheck>> baseData)
        {
             HealthCheckHeader header = new HealthCheckHeader();

             // Assuming groupedData is already populated and not empty
             var mlModelSelection = groupedData
                .FirstOrDefault()?
                .FirstOrDefault()?
                .MLModelSelection;


            var mlModelDate = groupedData
                .FirstOrDefault()?
                .FirstOrDefault()?
                .MLModelDate;


            var mltrgObject = groupedData
                .FirstOrDefault()?
                .FirstOrDefault()?
                .trgObject;

            header.MLModelDate = mlModelDate;
            header.trgObject = mltrgObject;
            header.MLModelSelection = mlModelSelection;
            header.MLModelSelectionParsed = ParseModelJson(mlModelSelection);

            return header; // Return empty string if no data is found
        }


        public HealthCheckHeader GetSingleMLModelSelection(IEnumerable<SQLFlowUi.Models.sqlflowProd.FlowHealthCheck> baseData )
        {
            HealthCheckHeader header = new HealthCheckHeader();

            // Assuming groupedData is already populated and not empty
            var mlModelSelection = baseData
                .FirstOrDefault()?
                .MLModelSelection;


            var mlModelDate = baseData
                .FirstOrDefault()?
                .MLModelDate;


            var mltrgObject = baseData
                .FirstOrDefault()?
                .trgObject;

            header.MLModelDate = mlModelDate;
            header.trgObject = mltrgObject;
            header.MLModelSelection = mlModelSelection;
            header.MLModelSelectionParsed = ParseModelJson(mlModelSelection);

            return header; // Return empty string if no data is found
        }


        public IEnumerable<ValidationModelData> ParseModelJson(string jsonString)
        {
            return JsonConvert.DeserializeObject<IEnumerable<ValidationModelData>>(jsonString);
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await dwSqlflowProdService.ExportReportFlowHealthCheckToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "ReportFlowHealthCheck1");
            }

            if (args == null || args.Value == "xlsx")
            {
                await dwSqlflowProdService.ExportReportFlowHealthCheckToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "ReportFlowHealthCheck1");
            }
        }
    }
}