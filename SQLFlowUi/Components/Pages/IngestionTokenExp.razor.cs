using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class IngestionTokenExp
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

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.IngestionTokenExp> ingestionTokenExp;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.IngestionTokenExp> grid0;

        protected string search = "";

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            ingestionTokenExp = await sqlflowProdService.GetIngestionTokenExp(new Query { Filter = $@"i => i.TokenExpAlias.Contains(@0) || i.SelectExp.Contains(@0) || i.SelectExpFull.Contains(@0) || i.DataType.Contains(@0) || i.Description.Contains(@0) || i.Example.Contains(@0)", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            ingestionTokenExp = await sqlflowProdService.GetIngestionTokenExp(new Query { Filter = $@"i => i.TokenExpAlias.Contains(@0) || i.SelectExp.Contains(@0) || i.SelectExpFull.Contains(@0) || i.DataType.Contains(@0) || i.Description.Contains(@0) || i.Example.Contains(@0)", FilterParameters = new object[] { search } });
        }

        protected async Task AddButtonClick(MouseEventArgs args)
        {
            await DialogService.OpenAsync<AddIngestionTokenExp>("Add IngestionTokenExp", null, GlobalSettings.EditOptions);
            await grid0.Reload();
        }

        protected async Task EditRow(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.IngestionTokenExp> args)
        {
            await DialogService.OpenAsync<EditIngestionTokenExp>("Edit IngestionTokenExp", new Dictionary<string, object> { {"TokenExpAlias", args.Data.TokenExpAlias} }, GlobalSettings.EditOptions);
        }

        protected async Task GridDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.IngestionTokenExp ingestionTokenExp)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await sqlflowProdService.DeleteIngestionTokenExp(ingestionTokenExp.TokenExpAlias);

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
                    Detail = $"Unable to delete IngestionTokenExp"
                });
            }
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportIngestionTokenExpToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "IngestionTokenExp");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportIngestionTokenExpToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "IngestionTokenExp");
            }
        }
    }
}