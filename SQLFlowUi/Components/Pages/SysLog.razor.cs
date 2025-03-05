using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class SysLog
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

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.SysLog> sysLog;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.SysLog> grid0;

        protected string search = "";

        [Parameter]
        public int? FlowID { get; set; }

        protected async Task EditUrl(SQLFlowUi.Models.sqlflowProd.SysLog args)
        {
            await DialogService.OpenAsync<EditSysLog>("Edit SysLog", new Dictionary<string, object> { { "FlowID", args.FlowID } }, GlobalSettings.EditOptions);
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender && FlowID.HasValue)
            {
                var ToEdit = sysLog.FirstOrDefault(p => p.FlowID == FlowID.Value);
                if (ToEdit != null)
                {
                    await EditUrl(ToEdit);
                }
            }
        }

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            sysLog = await sqlflowProdService.GetSysLog(new Query { Filter = $@"i => i.FlowType.Contains(@0) || i.ProcessShort.Contains(@0) || i.SysAlias.Contains(@0) || i.Batch.Contains(@0) || i.Process.Contains(@0) || i.FileName.Contains(@0) || i.FileDate.Contains(@0) || i.FileDateHist.Contains(@0) || i.ExecMode.Contains(@0) || i.SelectCmd.Contains(@0) || i.InsertCmd.Contains(@0) || i.UpdateCmd.Contains(@0) || i.DeleteCmd.Contains(@0) || i.RuntimeCmd.Contains(@0) || i.CreateCmd.Contains(@0) || i.SurrogateKeyCmd.Contains(@0) || i.ErrorInsert.Contains(@0) || i.ErrorUpdate.Contains(@0) || i.ErrorDelete.Contains(@0) || i.ErrorRuntime.Contains(@0) || i.FromObjectDef.Contains(@0) || i.ToObjectDef.Contains(@0) || i.PreProcessOnTrgDef.Contains(@0) || i.PostProcessOnTrgDef.Contains(@0) || i.AssertRowCount.Contains(@0) || i.WhereIncExp.Contains(@0) || i.WhereDateExp.Contains(@0) || i.TrgIndexes.Contains(@0)", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            sysLog = await sqlflowProdService.GetSysLog(new Query { Filter = $@"i => i.FlowType.Contains(@0) || i.ProcessShort.Contains(@0) || i.SysAlias.Contains(@0) || i.Batch.Contains(@0) || i.Process.Contains(@0) || i.FileName.Contains(@0) || i.FileDate.Contains(@0) || i.FileDateHist.Contains(@0) || i.ExecMode.Contains(@0) || i.SelectCmd.Contains(@0) || i.InsertCmd.Contains(@0) || i.UpdateCmd.Contains(@0) || i.DeleteCmd.Contains(@0) || i.RuntimeCmd.Contains(@0) || i.CreateCmd.Contains(@0) || i.SurrogateKeyCmd.Contains(@0) || i.ErrorInsert.Contains(@0) || i.ErrorUpdate.Contains(@0) || i.ErrorDelete.Contains(@0) || i.ErrorRuntime.Contains(@0) || i.FromObjectDef.Contains(@0) || i.ToObjectDef.Contains(@0) || i.PreProcessOnTrgDef.Contains(@0) || i.PostProcessOnTrgDef.Contains(@0) || i.AssertRowCount.Contains(@0) || i.WhereIncExp.Contains(@0) || i.WhereDateExp.Contains(@0) || i.TrgIndexes.Contains(@0)", FilterParameters = new object[] { search } });
        }

        protected async Task AddButtonClick(MouseEventArgs args)
        {
            await DialogService.OpenAsync<AddSysLog>("Add SysLog", null, GlobalSettings.EditOptions);
            await grid0.Reload();
        }

        protected async Task EditRow(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.SysLog> args)
        {
            await DialogService.OpenAsync<EditSysLog>("Edit SysLog", new Dictionary<string, object> { {"FlowID", args.Data.FlowID} }, GlobalSettings.EditOptions);
        }

        protected async Task GridDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.SysLog sysLog)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await sqlflowProdService.DeleteSysLog(sysLog.FlowID);

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
                    Detail = $"Unable to delete SysLog"
                });
            }
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportSysLogToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "SysLog");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportSysLogToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "SysLog");
            }
        }
    }
}