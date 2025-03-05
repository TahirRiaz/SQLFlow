using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class SysSourceControlType
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

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.SysSourceControlType> sysSourceControlType;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.SysSourceControlType> grid0;

        protected string search = "";

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            sysSourceControlType = await sqlflowProdService.GetSysSourceControlType(new Query { Filter = $@"i => i.SourceControlType.Contains(@0) || i.SCAlias.Contains(@0) || i.Username.Contains(@0) || i.AccessToken.Contains(@0) || i.ConsumerKey.Contains(@0) || i.ConsumerSecret.Contains(@0) || i.WorkSpaceName.Contains(@0) || i.ProjectName.Contains(@0) || i.ProjectKey.Contains(@0)", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            sysSourceControlType = await sqlflowProdService.GetSysSourceControlType(new Query { Filter = $@"i => i.SourceControlType.Contains(@0) || i.SCAlias.Contains(@0) || i.Username.Contains(@0) || i.AccessToken.Contains(@0) || i.ConsumerKey.Contains(@0) || i.ConsumerSecret.Contains(@0) || i.WorkSpaceName.Contains(@0) || i.ProjectName.Contains(@0) || i.ProjectKey.Contains(@0)", FilterParameters = new object[] { search } });
        }

        protected async Task AddButtonClick(MouseEventArgs args)
        {
            await DialogService.OpenAsync<AddSysSourceControlType>("Add SysSourceControlType", null, GlobalSettings.EditOptions);
            await grid0.Reload();
        }

        protected async Task EditRow(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.SysSourceControlType> args)
        {
            await DialogService.OpenAsync<EditSysSourceControlType>("Edit SysSourceControlType", new Dictionary<string, object> { {"SourceControlTypeID", args.Data.SourceControlTypeID} }, GlobalSettings.EditOptions);
        }

        protected async Task GridDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.SysSourceControlType sysSourceControlType)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await sqlflowProdService.DeleteSysSourceControlType(sysSourceControlType.SourceControlTypeID);

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
                    Detail = $"Unable to delete SysSourceControlType"
                });
            }
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportSysSourceControlTypeToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "SysSourceControlType");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportSysSourceControlTypeToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "SysSourceControlType");
            }
        }
    }
}