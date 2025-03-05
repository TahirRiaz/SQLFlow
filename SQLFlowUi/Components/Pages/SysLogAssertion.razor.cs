using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class SysLogAssertion
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
        public sqlflowProdService SqlflowProdService { get; set; }

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.SysLogAssertion> sysLogAssertion;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.SysLogAssertion> grid0;

        protected string search = "";

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            sysLogAssertion = await SqlflowProdService.GetSysLogAssertion(new Query { Filter = $@"i => i.AssertionSqlCmd.Contains(@0) || i.Result.Contains(@0) || i.TraceLog.Contains(@0)", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            sysLogAssertion = await SqlflowProdService.GetSysLogAssertion(new Query { Filter = $@"i => i.AssertionSqlCmd.Contains(@0) || i.Result.Contains(@0) || i.TraceLog.Contains(@0)", FilterParameters = new object[] { search } });
        }

        protected async Task AddButtonClick(MouseEventArgs args)
        {
            await DialogService.OpenAsync<AddSysLogAssertion>("Add SysLogAssertion", null);
            await grid0.Reload();
        }

        protected async Task EditRow(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.SysLogAssertion> args)
        {
            await DialogService.OpenAsync<EditSysLogAssertion>("Edit SysLogAssertion", new Dictionary<string, object> { {"RecID", args.Data.RecID} });
        }

        protected async Task GridDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.SysLogAssertion sysLogAssertion)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await SqlflowProdService.DeleteSysLogAssertion(sysLogAssertion.RecID);

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
                    Detail = $"Unable to delete SysLogAssertion"
                });
            }
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await SqlflowProdService.ExportSysLogAssertionToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "SysLogAssertion");
            }

            if (args == null || args.Value == "xlsx")
            {
                await SqlflowProdService.ExportSysLogAssertionToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "SysLogAssertion");
            }
        }
    }
}