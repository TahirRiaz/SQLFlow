using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class Invoke
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

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.Invoke> invoke;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.Invoke> grid0;

        protected string search = "";
        [Parameter]
        public int? FlowID { get; set; }


        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender && FlowID.HasValue)
            {
                var ToEdit = invoke.FirstOrDefault(p => p.FlowID == FlowID.Value);
                if (ToEdit != null)
                {
                    await EditUrl(ToEdit);
                }
            }
        }

        protected async Task EditUrl(SQLFlowUi.Models.sqlflowProd.Invoke args)
        {
            await DialogService.OpenAsync<EditInvoke>("Edit Invoke", new Dictionary<string, object> { { "FlowID", args.FlowID } }, GlobalSettings.EditOptions);
        }

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            invoke = await sqlflowProdService.GetInvoke(new Query { Filter = $@"i => i.FlowID.ToString().Contains(@0) || i.SysAlias.Contains(@0) ||  i.srcServicePrincipalAlias.Contains(@0) ||  i.trgServicePrincipalAlias.Contains(@0) || i.InvokeAlias.Contains(@0) || i.InvokeType.Contains(@0) || i.InvokePath.Contains(@0) || i.InvokeFile.Contains(@0) || i.Code.Contains(@0) || i.Arguments.Contains(@0) || i.PipelineName.Contains(@0) || i.RunbookName.Contains(@0) || i.ParameterJSON.Contains(@0) || i.Batch.Contains(@0)", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            invoke = await sqlflowProdService.GetInvoke(new Query { Filter = $@"i => i.FlowID.ToString().Contains(@0) || i.SysAlias.Contains(@0) ||  i.srcServicePrincipalAlias.Contains(@0) ||  i.trgServicePrincipalAlias.Contains(@0) || i.InvokeAlias.Contains(@0) || i.InvokeType.Contains(@0) || i.InvokePath.Contains(@0) || i.InvokeFile.Contains(@0) || i.Code.Contains(@0) || i.Arguments.Contains(@0) || i.PipelineName.Contains(@0) || i.RunbookName.Contains(@0) || i.ParameterJSON.Contains(@0) || i.Batch.Contains(@0)", FilterParameters = new object[] { search } });
        }

        protected async Task AddButtonClick(MouseEventArgs args)
        {
            await DialogService.OpenAsync<AddInvoke>("Add Invoke", null, GlobalSettings.EditOptions);
            await grid0.Reload();
        }

        protected async Task EditRow(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.Invoke> args)
        {
            await DialogService.OpenAsync<EditInvoke>("Edit Invoke", new Dictionary<string, object> { {"FlowID", args.Data.FlowID} }, GlobalSettings.EditOptions);
        }

        protected async Task GridDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.Invoke invoke)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await sqlflowProdService.DeleteInvoke(invoke.FlowID);

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
                    Detail = $"Unable to delete Invoke"
                });
            }
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportInvokeToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "Invoke");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportInvokeToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "Invoke");
            }
        }
    }
}