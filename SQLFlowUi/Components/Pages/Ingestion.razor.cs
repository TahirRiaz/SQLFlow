using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class Ingestion
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
        public int? FlowID { get; set; }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender && FlowID.HasValue)
            {
                var ToEdit = ingestion.FirstOrDefault(p => p.FlowID == FlowID.Value);
                if (ToEdit != null)
                {
                    await EditUrl(ToEdit);
                }
            }
        }

        protected async Task EditUrl(SQLFlowUi.Models.sqlflowProd.Ingestion args)
        {
            await DialogService.OpenAsync<EditIngestion>("Edit Ingestion", new Dictionary<string, object> { { "FlowID", args.FlowID } }, GlobalSettings.EditOptions);
        }

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.Ingestion> ingestion;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.Ingestion> grid0;


        protected string search = "";

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            ingestion = await sqlflowProdService.GetIngestion(new Query { Filter = $@"i => i.FlowID.ToString().Contains(@0) || i.SysAlias.Contains(@0) || i.srcServer.Contains(@0) || i.srcDBSchTbl.Contains(@0) || i.trgServer.Contains(@0) || i.trgDBSchTbl.Contains(@0) || i.Batch.Contains(@0) || i.KeyColumns.Contains(@0) || i.IncrementalColumns.Contains(@0) || i.IncrementalClauseExp.Contains(@0) || i.DateColumn.Contains(@0) || i.DataSetColumn.Contains(@0) || i.srcFilter.Contains(@0) || i.IdentityColumn.Contains(@0) || i.HashKeyColumns.Contains(@0) || i.HashKeyType.Contains(@0) || i.IgnoreColumns.Contains(@0) || i.SysColumns.Contains(@0) || i.CleanColumnNameSQLRegExp.Contains(@0) || i.PreProcessOnTrg.Contains(@0) || i.PostProcessOnTrg.Contains(@0) || i.PreInvokeAlias.Contains(@0) || i.PostInvokeAlias.Contains(@0) || i.FlowType.Contains(@0) || i.Description.Contains(@0) || i.CreatedBy.Contains(@0)", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            ingestion = await sqlflowProdService.GetIngestion(new Query { Filter = $@"i => i.FlowID.ToString().Contains(@0) || i.SysAlias.Contains(@0) || i.srcServer.Contains(@0) || i.srcDBSchTbl.Contains(@0) || i.trgServer.Contains(@0) || i.trgDBSchTbl.Contains(@0) || i.Batch.Contains(@0) || i.KeyColumns.Contains(@0) || i.IncrementalColumns.Contains(@0) || i.IncrementalClauseExp.Contains(@0) || i.DateColumn.Contains(@0) || i.DataSetColumn.Contains(@0) || i.srcFilter.Contains(@0) || i.IdentityColumn.Contains(@0) || i.HashKeyColumns.Contains(@0) || i.HashKeyType.Contains(@0) || i.IgnoreColumns.Contains(@0) || i.SysColumns.Contains(@0) || i.CleanColumnNameSQLRegExp.Contains(@0) || i.PreProcessOnTrg.Contains(@0) || i.PostProcessOnTrg.Contains(@0) || i.PreInvokeAlias.Contains(@0) || i.PostInvokeAlias.Contains(@0) || i.FlowType.Contains(@0) || i.Description.Contains(@0) || i.CreatedBy.Contains(@0)", FilterParameters = new object[] { search } });
        }

        protected async Task AddButtonClick(MouseEventArgs args)
        {
            await DialogService.OpenAsync<AddIngestion>("Add Ingestion", null, GlobalSettings.EditOptions);
            await grid0.Reload();
        }

        protected async Task EditRow(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.Ingestion> args)
        {
            await DialogService.OpenAsync<EditIngestion>("Edit Ingestion", new Dictionary<string, object> { {"FlowID", args.Data.FlowID} }, GlobalSettings.EditOptions);
        }

        protected async Task GridDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.Ingestion ingestion)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await sqlflowProdService.DeleteIngestion(ingestion.FlowID);

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
                    Detail = $"Unable to delete Ingestion"
                });
            }
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportIngestionToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "Ingestion");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportIngestionToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "Ingestion");
            }
        }
    }
}