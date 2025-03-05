using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class PreIngestionADOVirtual
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

        [Parameter]
        public int? VirtualID { get; set; }


        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual> preIngestionADOVirtual;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual> grid0;

        protected string search = "";


        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender && VirtualID.HasValue)
            {
                var ToEdit = preIngestionADOVirtual.FirstOrDefault(p => p.VirtualID == VirtualID.Value);
                if (ToEdit != null)
                {
                    await EditUrl(ToEdit);
                }
            }
        }

        protected async Task EditUrl(SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual args)
        {
            await DialogService.OpenAsync<EditPreIngestionTransfrom>("Edit PreIngestionTransfrom", new Dictionary<string, object> { { "TransfromID", args.VirtualID } }, GlobalSettings.EditOptions);
        }

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            preIngestionADOVirtual = await sqlflowProdService.GetPreIngestionADOVirtual(new Query { Filter = $@"i => i.ColumnName.Contains(@0) || i.DataType.Contains(@0) || i.SelectExp.Contains(@0)", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            preIngestionADOVirtual = await sqlflowProdService.GetPreIngestionADOVirtual(new Query { Filter = $@"i => i.ColumnName.Contains(@0) || i.DataType.Contains(@0) || i.SelectExp.Contains(@0)", FilterParameters = new object[] { search } });
        }

        protected async Task AddButtonClick(MouseEventArgs args)
        {
            await DialogService.OpenAsync<AddPreIngestionADOVirtual>("Add PreIngestionADOVirtual", null, GlobalSettings.EditOptions);
            await grid0.Reload();
        }

        protected async Task EditRow(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual> args)
        {
            await DialogService.OpenAsync<EditPreIngestionADOVirtual>("Edit PreIngestionADOVirtual", new Dictionary<string, object> { {"VirtualID", args.Data.VirtualID} }, GlobalSettings.EditOptions);
        }

        protected async Task GridDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual preIngestionADOVirtual)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await sqlflowProdService.DeletePreIngestionADOVirtual(preIngestionADOVirtual.VirtualID);

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
                    Detail = $"Unable to delete PreIngestionADOVirtual"
                });
            }
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportPreIngestionADOVirtualToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "PreIngestionADOVirtual");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportPreIngestionADOVirtualToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "PreIngestionADOVirtual");
            }
        }
    }
}