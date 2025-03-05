using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class LineageMap
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
        public sqlflowProdService sqlflowProdService { get; set; }

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.LineageMap> lineageMap;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.LineageMap> grid0;

        protected string search = "";

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            lineageMap = await sqlflowProdService.GetLineageMap(new Query { Filter = $@"i => i.Batch.Contains(@0) || i.SysAlias.Contains(@0) || i.FlowType.Contains(@0) || i.FromObject.Contains(@0) || i.ToObject.Contains(@0) || i.PathStr.Contains(@0) || i.PathNum.Contains(@0) || i.RootObject.Contains(@0) || i.FromObjectType.Contains(@0) || i.ToObjectType.Contains(@0) || i.LatestFileProcessed.Contains(@0) || i.NextStepFlows.Contains(@0)", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            lineageMap = await sqlflowProdService.GetLineageMap(new Query { Filter = $@"i => i.Batch.Contains(@0) || i.SysAlias.Contains(@0) || i.FlowType.Contains(@0) || i.FromObject.Contains(@0) || i.ToObject.Contains(@0) || i.PathStr.Contains(@0) || i.PathNum.Contains(@0) || i.RootObject.Contains(@0) || i.FromObjectType.Contains(@0) || i.ToObjectType.Contains(@0) || i.LatestFileProcessed.Contains(@0) || i.NextStepFlows.Contains(@0)", FilterParameters = new object[] { search } });
        }

        protected async Task AddButtonClick(MouseEventArgs args)
        {
            await DialogService.OpenAsync<AddLineageMap>("Add LineageMap", null, GlobalSettings.EditOptions);
            await grid0.Reload();
        }

        protected async Task EditRow(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.LineageMap> args)
        {
            await DialogService.OpenAsync<EditLineageMap>("Edit LineageMap", new Dictionary<string, object> { {"LineageParsedID", args.Data.LineageParsedID} }, GlobalSettings.EditOptions);
        }

        protected async Task GridDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.LineageMap lineageMap)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await sqlflowProdService.DeleteLineageMap(lineageMap.LineageParsedID);

                    if (deleteResult != null)
                    {
                        await grid0.Reload();
                    }
                }
            }
            catch (Exception ex)
            {
                NotificationService.Notify(new NotificationMessage
                {
                    Severity = NotificationSeverity.Error,
                    Summary = $"Error",
                    Detail = $"Unable to delete LineageMap"
                });
            }
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportLineageMapToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "LineageMap");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportLineageMapToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "LineageMap");
            }
        }
    }
}