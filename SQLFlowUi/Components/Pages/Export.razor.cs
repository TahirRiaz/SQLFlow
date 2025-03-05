using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class Export
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

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.Export> export;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.Export> grid0;

        protected string search = "";

        [Parameter]
        public int? FlowID { get; set; }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender && FlowID.HasValue)
            {
                var ToEdit = export.FirstOrDefault(p => p.FlowID == FlowID.Value);
                if (ToEdit != null)
                {
                    await EditUrl(ToEdit);
                }
            }
        }

        protected async Task EditUrl(SQLFlowUi.Models.sqlflowProd.Export args)
        {
            await DialogService.OpenAsync<EditExport>("Edit Export", new Dictionary<string, object> { { "FlowID", args.FlowID } }, GlobalSettings.EditOptions);
        }

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            export = await sqlflowProdService.GetExport(new Query { Filter = $@"i => i.FlowID.ToString().Contains(@0) || i.SysAlias.Contains(@0) || i.Batch.Contains(@0) || i.srcServer.Contains(@0) || i.srcDBSchTbl.Contains(@0) || i.srcWithHint.Contains(@0) || i.srcFilter.Contains(@0) || i.IncrementalColumn.Contains(@0) || i.DateColumn.Contains(@0) || i.ExportBy.Contains(@0)  || i.ServicePrincipalAlias.Contains(@0) || i.trgPath.Contains(@0) || i.trgFileName.Contains(@0) || i.trgFiletype.Contains(@0) || i.trgEncoding.Contains(@0) || i.CompressionType.Contains(@0) || i.ColumnDelimiter.Contains(@0) || i.TextQualifier.Contains(@0) || i.Subfolderpattern.Contains(@0) || i.PostInvokeAlias.Contains(@0) || i.FlowType.Contains(@0) || i.CreatedBy.Contains(@0)", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            export = await sqlflowProdService.GetExport(new Query { Filter = $@"i => i.FlowID.ToString().Contains(@0) || i.SysAlias.Contains(@0) || i.Batch.Contains(@0) || i.srcServer.Contains(@0) || i.srcDBSchTbl.Contains(@0) || i.srcWithHint.Contains(@0) || i.srcFilter.Contains(@0) || i.IncrementalColumn.Contains(@0) || i.DateColumn.Contains(@0) || i.ExportBy.Contains(@0) || i.ServicePrincipalAlias.Contains(@0) || i.trgPath.Contains(@0) || i.trgFileName.Contains(@0) || i.trgFiletype.Contains(@0) || i.trgEncoding.Contains(@0) || i.CompressionType.Contains(@0) || i.ColumnDelimiter.Contains(@0) || i.TextQualifier.Contains(@0) || i.Subfolderpattern.Contains(@0) || i.PostInvokeAlias.Contains(@0) || i.FlowType.Contains(@0) || i.CreatedBy.Contains(@0)", FilterParameters = new object[] { search } });
        }

        protected async Task AddButtonClick(MouseEventArgs args)
        {
            await DialogService.OpenAsync<AddExport>("Add Export", null, GlobalSettings.EditOptions);
            await grid0.Reload();
        }

        protected async Task EditRow(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.Export> args)
        {
            await DialogService.OpenAsync<EditExport>("Edit Export", new Dictionary<string, object> { {"FlowID", args.Data.FlowID} }, GlobalSettings.EditOptions);
        }

        protected async Task GridDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.Export export)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await sqlflowProdService.DeleteExport(export.FlowID);

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
                    Detail = $"Unable to delete Export"
                });
            }
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportExportToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "Export");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportExportToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "Export");
            }
        }
    }
}