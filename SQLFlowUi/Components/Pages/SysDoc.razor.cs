using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class SysDoc
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

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.SysDoc> sysDoc;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.SysDoc> grid0;

        protected string search = "";

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            sysDoc = await sqlflowProdService.GetSysDoc(new Query { Filter = $@"i => i.ObjectName.Contains(@0) || i.ObjectType.Contains(@0) ", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            sysDoc = await sqlflowProdService.GetSysDoc(new Query { Filter = $@"i => i.ObjectName.Contains(@0) || i.ObjectType.Contains(@0) ", FilterParameters = new object[] { search } });
        }

        protected async Task AddButtonClick(MouseEventArgs args)
        {
            await DialogService.OpenAsync<AddSysDoc>("Add SysDoc", null, GlobalSettings.EditOptions);
            await grid0.Reload();
        }

        protected async Task EditRow(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.SysDoc> args)
        {
            await DialogService.OpenAsync<EditSysDoc>("Edit SysDoc", new Dictionary<string, object> { {"SysDocID", args.Data.SysDocID} }, GlobalSettings.EditOptions);
        }


        public async Task OpenSysDoc(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.SysDoc sysDoc)
        {
            var parameters = new Dictionary<string, object>
            {
                { "ObjectName", sysDoc.ObjectName }
            };
            

            await DialogService.OpenAsync<SysDocModal>($"Documentation {sysDoc.ObjectName}",
                parameters,
                new DialogOptions() { Width = "1200px", Height = "760px", Resizable = true, Draggable = true });
        }

        protected async Task GridDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.SysDoc sysDoc)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await sqlflowProdService.DeleteSysDoc(sysDoc.SysDocID);

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
                    Detail = $"Unable to delete SysDoc"
                });
            }
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportSysDocToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "SysDoc");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportSysDocToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "SysDoc");
            }
        }
    }
}