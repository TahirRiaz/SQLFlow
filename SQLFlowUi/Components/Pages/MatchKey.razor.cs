using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class MatchKey
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

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.MatchKey> matchKey;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.MatchKey> grid0;

        protected string search = "";


        [Parameter]
        public int? MatchKeyID { get; set; }


        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender && MatchKeyID.HasValue)
            {
                var ToEdit = matchKey.FirstOrDefault(p => p.MatchKeyID == MatchKeyID.Value);
                if (ToEdit != null)
                {
                    await EditUrl(ToEdit);
                }
            }
        }

        protected async Task EditUrl(SQLFlowUi.Models.sqlflowProd.MatchKey args)
        {
            await DialogService.OpenAsync<EditMatchKey>("Edit Match Key", new Dictionary<string, object> { { "MatchKeyID", args.MatchKeyID } }, GlobalSettings.EditOptions);
        }

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            matchKey = await sqlflowProdService.GetMatchKey(new Query { Filter = $@"i => i.Batch.Contains(@0) || i.SysAlias.Contains(@0) || i.srcServer.Contains(@0) || i.srcDatabase.Contains(@0) || i.srcSchema.Contains(@0) || i.srcObject.Contains(@0) || i.trgServer.Contains(@0) || i.trgDBSchTbl.Contains(@0) || i.DateColumn.Contains(@0) || i.srcFilter.Contains(@0) || i.PreProcessOnTrg.Contains(@0) || i.PostProcessOnTrg.Contains(@0) || i.Description.Contains(@0) || i.CreatedBy.Contains(@0)", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            matchKey = await sqlflowProdService.GetMatchKey(new Query { Filter = $@"i => i.Batch.Contains(@0) || i.SysAlias.Contains(@0) || i.srcServer.Contains(@0) || i.srcDatabase.Contains(@0) || i.srcSchema.Contains(@0) || i.srcObject.Contains(@0) || i.trgServer.Contains(@0) || i.trgDBSchTbl.Contains(@0) || i.DateColumn.Contains(@0) || i.srcFilter.Contains(@0) || i.PreProcessOnTrg.Contains(@0) || i.PostProcessOnTrg.Contains(@0) || i.Description.Contains(@0) || i.CreatedBy.Contains(@0)", FilterParameters = new object[] { search } });
        }

        protected async Task AddButtonClick(MouseEventArgs args)
        {
            await DialogService.OpenAsync<AddMatchKey>("Add MatchKey", null, GlobalSettings.EditOptions);
            await grid0.Reload();
        }

        protected async Task EditRow(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.MatchKey> args)
        {
            await DialogService.OpenAsync<EditMatchKey>("Edit MatchKey", new Dictionary<string, object> { {"MatchKeyID", args.Data.MatchKeyID} }, GlobalSettings.EditOptions);
        }

        protected async Task GridDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.MatchKey matchKey)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await sqlflowProdService.DeleteMatchKey(matchKey.MatchKeyID);

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
                    Detail = $"Unable to delete MatchKey"
                });
            }
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportMatchKeyToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "MatchKey");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportMatchKeyToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "MatchKey");
            }
        }
    }
}