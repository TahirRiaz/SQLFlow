using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class ReportAssertionX
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

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.ReportAssertion> reportAssertion;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.ReportAssertion> grid0;

        protected string search = "";

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            reportAssertion = await sqlflowProdService.GetReportAssertion(new Query { Filter = $@"i => i.AssertionName.Contains(@0) || i.AssertionExp.Contains(@0) || i.AssertionSqlCmd.Contains(@0) || i.Result.Contains(@0) || i.TraceLog.Contains(@0)", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            reportAssertion = await sqlflowProdService.GetReportAssertion(new Query { Filter = $@"i => i.AssertionName.Contains(@0) || i.AssertionExp.Contains(@0) || i.AssertionSqlCmd.Contains(@0) || i.Result.Contains(@0) || i.TraceLog.Contains(@0)", FilterParameters = new object[] { search } });
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportReportAssertionToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "ReportAssertion");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportReportAssertionToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "ReportAssertion");
            }
        }
    }
}