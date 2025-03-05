using System.Net;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.EntityFrameworkCore;
using Radzen;
using Radzen.Blazor;


namespace SQLFlowUi.Components.Pages
{
    public partial class SysLogFile
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

        [Inject] 
        private SQLFlowUi.Data.sqlflowProdContext _context { get; set; }

        [Parameter]
        public string searchTerm { get; set; } = "";

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.SysLogFile> sysLogFile;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.SysLogFile> grid0;

        protected string search = "";

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";
        }

        protected async System.Threading.Tasks.Task Button1Click(Microsoft.AspNetCore.Components.Web.MouseEventArgs args)
        {
            if (search.Length > 1)
            {
                await grid0.GoToPage(0);
                sysLogFile = _context.SysLogFile.FromSqlRaw("SELECT * FROM flw.SysLogFile WHERE CONTAINS(FileName_DW, {0})", search);
            }
        }

        protected override async Task OnParametersSetAsync()
        {
            if (!string.IsNullOrEmpty(searchTerm))
            {
                string decodedString = Decode(searchTerm);

                sysLogFile = _context.SysLogFile.FromSqlRaw("SELECT * FROM flw.SysLogFile WHERE CONTAINS(FileName_DW, {0})", decodedString);
                search = decodedString;
            }
        }

        protected string Decode(string text)
        {
           return WebUtility.UrlDecode(text);
        }

        protected override async Task OnInitializedAsync()
        {
            sysLogFile = await sqlflowProdService.GetSysLogFile(new Query { Filter = $@"i => i.BatchID.Contains(@0) || i.FileDate_DW.Contains(@0) || i.FileName_DW.Contains(@0) || i.DataSet_DW.Contains(@0)", FilterParameters = new object[] { search } });
        }

        protected async Task AddButtonClick(MouseEventArgs args)
        {
            await DialogService.OpenAsync<AddSysLogFile>("Add SysLogFile", null, GlobalSettings.EditOptions);
            await grid0.Reload();
        }

        protected async Task EditRow(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.SysLogFile> args)
        {
            await DialogService.OpenAsync<EditSysLogFile>("Edit SysLogFile", new Dictionary<string, object> { {"RecID", args.Data.LogFileID} }, GlobalSettings.EditOptions);
        }

        protected async Task GridDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.SysLogFile sysLogFile)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await sqlflowProdService.DeleteSysLogFile(sysLogFile.LogFileID);

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
                    Detail = $"Unable to delete SysLogFile"
                });
            }
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportSysLogFileToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "SysLogFile");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportSysLogFileToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "SysLogFile");
            }
        }


    }
}