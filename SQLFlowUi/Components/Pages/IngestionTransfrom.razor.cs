using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class IngestionTransfrom
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

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.IngestionTransfrom> ingestionTransfrom;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.IngestionTransfrom> grid0;

        protected string search = "";

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            ingestionTransfrom = await sqlflowProdService.GetIngestionTransfrom(new Query { Filter = $@"i => i.FlowID.ToString().Contains(@0) || i.ColumnName.Contains(@0) || i.DataTypeExp.Contains(@0) || i.SelectExp.Contains(@0) ", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            ingestionTransfrom = await sqlflowProdService.GetIngestionTransfrom(new Query { Filter = $@"i => i.FlowID.ToString().Contains(@0) || i.ColumnName.Contains(@0) || i.DataTypeExp.Contains(@0) || i.SelectExp.Contains(@0) ", FilterParameters = new object[] { search } });
        }

        protected async Task AddButtonClick(MouseEventArgs args)
        {
            await DialogService.OpenAsync<AddIngestionTransfrom>("Add IngestionTransfrom", null, GlobalSettings.EditOptions);
            await grid0.Reload();
        }

        protected async Task EditRow(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.IngestionTransfrom> args)
        {
            await DialogService.OpenAsync<EditIngestionTransfrom>("Edit IngestionTransfrom", new Dictionary<string, object> { {"TransfromID", args.Data.TransfromID} }, GlobalSettings.EditOptions);
        }

        protected async Task GridDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.IngestionTransfrom ingestionTransfrom)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await sqlflowProdService.DeleteIngestionTransfrom(ingestionTransfrom.TransfromID);

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
                    Detail = $"Unable to delete IngestionTransfrom"
                });
            }
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportIngestionTransfromToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "IngestionTransfrom");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportIngestionTransfromToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "IngestionTransfrom");
            }
        }
    }
}