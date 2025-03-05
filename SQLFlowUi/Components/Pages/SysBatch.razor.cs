using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class SysBatch
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

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.SysBatch> sysBatch;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.SysBatch> grid0;

        protected string search = "";

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            sysBatch = await sqlflowProdService.GetSysBatch(new Query { Filter = $@"i => i.Batch.Contains(@0)", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            sysBatch = await sqlflowProdService.GetSysBatch(new Query { Filter = $@"i => i.Batch.Contains(@0)", FilterParameters = new object[] { search } });
        }

        protected async Task AddButtonClick(MouseEventArgs args)
        {
            await DialogService.OpenAsync<AddSysBatch>("Add SysBatch", null);
            await grid0.Reload();
        }

        protected async Task EditRow(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.SysBatch> args)
        {
            await DialogService.OpenAsync<EditSysBatch>("Edit SysBatch", new Dictionary<string, object> { {"SysBatchID", args.Data.SysBatchID} });
        }

        protected async Task GridDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.SysBatch sysBatch)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await sqlflowProdService.DeleteSysBatch(sysBatch.SysBatchID);

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
                    Detail = $"Unable to delete SysBatch"
                });
            }
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportSysBatchToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "SysBatch");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportSysBatchToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "SysBatch");
            }
        }
    }
}