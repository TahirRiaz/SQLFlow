using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class FlowDs
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

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.FlowDS> flowDs;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.FlowDS> grid0;

        protected string search = "";

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            flowDs = await sqlflowProdService.GetFlowDs(new Query { Filter = $@"i => i.FlowType.Contains(@0) || i.SysAlias.Contains(@0) || i.Batch.Contains(@0) || i.trgDBSchObj.Contains(@0) || i.trgDatabase.Contains(@0) || i.trgSchema.Contains(@0) || i.trgObject.Contains(@0) || i.SourceType.Contains(@0) || i.DatabaseName.Contains(@0) || i.Alias.Contains(@0) || i.preFilter.Contains(@0) || i.Process.Contains(@0) || i.ProcessShort.Contains(@0)", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            flowDs = await sqlflowProdService.GetFlowDs(new Query { Filter = $@"i => i.FlowType.Contains(@0) || i.SysAlias.Contains(@0) || i.Batch.Contains(@0) || i.trgDBSchObj.Contains(@0) || i.trgDatabase.Contains(@0) || i.trgSchema.Contains(@0) || i.trgObject.Contains(@0) || i.SourceType.Contains(@0) || i.DatabaseName.Contains(@0) || i.Alias.Contains(@0) || i.preFilter.Contains(@0) || i.Process.Contains(@0) || i.ProcessShort.Contains(@0)", FilterParameters = new object[] { search } });
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportFlowDsToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "FlowDs");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportFlowDsToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "FlowDs");
            }
        }
    }
}