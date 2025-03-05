#nullable enable
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class ReportBatchStartEnd
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

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.ReportBatchStartEnd> reportBatchStartEnd;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.ReportBatchStartEnd> grid0;

        protected string search = "";

        [Parameter]
        public string? urlsearch { get; set; } 

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            reportBatchStartEnd = await sqlflowProdService.GetReportBatchStartEnd(new Query { Filter = $@"i => i.Batch.Contains(@0) || i.ProcessShort.Contains(@0) ", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            if (urlsearch != null)
            {
                search = urlsearch;
                //StateHasChanged();
            }
            reportBatchStartEnd = await sqlflowProdService.GetReportBatchStartEnd(new Query { Filter = $@"i => i.Batch.Contains(@0) || i.ProcessShort.Contains(@0) ", FilterParameters = new object[] { search } });
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportReportBatchStartEndToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "ReportBatchStartEnd");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportReportBatchStartEndToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "ReportBatchStartEnd");
            }
        }
    }
}