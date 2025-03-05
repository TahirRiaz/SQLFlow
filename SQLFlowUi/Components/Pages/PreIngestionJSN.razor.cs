using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class PreIngestionJSN
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

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN> preIngestionJSN;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN> grid0;

        protected string search = "";

        [Parameter]
        public int? FlowID { get; set; }


        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender && FlowID.HasValue)
            {
                var ToEdit = preIngestionJSN.FirstOrDefault(p => p.FlowID == FlowID.Value);
                if (ToEdit != null)
                {
                    await EditUrl(ToEdit);
                }
            }
        }

        protected async Task EditUrl(SQLFlowUi.Models.sqlflowProd.PreIngestionJSN args)
        {
            await DialogService.OpenAsync<EditPreIngestionJSN>("Edit PreIngestionJsn", new Dictionary<string, object> { { "FlowID", args.FlowID } }, GlobalSettings.EditOptions);
        }

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            preIngestionJSN = await sqlflowProdService.GetPreIngestionJSN(new Query { Filter = $@"i => i.FlowID.ToString().Contains(@0) || i.SysAlias.Contains(@0) || i.Batch.Contains(@0) || i.ServicePrincipalAlias.Contains(@0) || i.srcPath.Contains(@0) || i.srcPathMask.Contains(@0) || i.srcFile.Contains(@0) || i.trgServer.Contains(@0) || i.trgDBSchTbl.Contains(@0) || i.preFilter.Contains(@0) || i.copyToPath.Contains(@0) || i.zipToPath.Contains(@0) || i.DefaultColDataType.Contains(@0) || i.PreProcessOnTrg.Contains(@0) || i.PostProcessOnTrg.Contains(@0) || i.PreInvokeAlias.Contains(@0) || i.InitFromFileDate.Contains(@0) || i.InitToFileDate.Contains(@0) || i.FlowType.Contains(@0) || i.CreatedBy.Contains(@0)", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            preIngestionJSN = await sqlflowProdService.GetPreIngestionJSN(new Query { Filter = $@"i => i.FlowID.ToString().Contains(@0) || i.SysAlias.Contains(@0) || i.Batch.Contains(@0) || i.ServicePrincipalAlias.Contains(@0) || i.srcPath.Contains(@0) || i.srcPathMask.Contains(@0) || i.srcFile.Contains(@0) || i.trgServer.Contains(@0) || i.trgDBSchTbl.Contains(@0) || i.preFilter.Contains(@0) || i.copyToPath.Contains(@0) || i.zipToPath.Contains(@0) || i.DefaultColDataType.Contains(@0) || i.PreProcessOnTrg.Contains(@0) || i.PostProcessOnTrg.Contains(@0) || i.PreInvokeAlias.Contains(@0) || i.InitFromFileDate.Contains(@0) || i.InitToFileDate.Contains(@0) || i.FlowType.Contains(@0) || i.CreatedBy.Contains(@0)", FilterParameters = new object[] { search } });
        }

        protected async Task AddButtonClick(MouseEventArgs args)
        {
            await DialogService.OpenAsync<AddPreIngestionJSN>("Add PreIngestionJSN", null, GlobalSettings.EditOptions);
            await grid0.Reload();
        }

        protected async Task EditRow(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.PreIngestionJSN> args)
        {
            await DialogService.OpenAsync<EditPreIngestionJSN>("Edit PreIngestionJSN", new Dictionary<string, object> { {"FlowID", args.Data.FlowID} }, GlobalSettings.EditOptions);
        }

        protected async Task GridDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.PreIngestionJSN preIngestionJSN)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await sqlflowProdService.DeletePreIngestionJSN(preIngestionJSN.FlowID);

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
                    Detail = $"Unable to delete PreIngestionJSN"
                });
            }
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportPreIngestionJSNToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "PreIngestionJSN");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportPreIngestionJSNToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "PreIngestionJSN");
            }
        }
    }
}