using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class DataSubscriberGrid
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

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.DataSubscriber> dataSubscriberX;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.DataSubscriber> grid0;

        protected string search = "";

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            dataSubscriberX = await sqlflowProdService.GetDataSubscriber(new Query { Filter = $@"i => i.SubscriberName.Contains(@0) ", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            dataSubscriberX = await sqlflowProdService.GetDataSubscriber(new Query { Filter = $@"i => i.SubscriberName.Contains(@0) ", FilterParameters = new object[] { search } });
        }

        protected async Task AddButtonClick(MouseEventArgs args)
        {
            await DialogService.OpenAsync<AddDataSubscriber>("Add DataSubscriber", null);
            await grid0.Reload();
        }

        protected async Task EditRow(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.DataSubscriber> args)
        {
            await DialogService.OpenAsync<EditDataSubscriber>("Edit DataSubscriber", new Dictionary<string, object> { {"FlowID", args.Data.FlowID} });
        }

        protected async Task GridDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.DataSubscriber dataSubscriber)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await sqlflowProdService.DeleteDataSubscriber(dataSubscriber.FlowID);

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
                    Detail = $"Unable to delete DataSubscriber"
                });
            }
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportDataSubscriberToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "DataSubscriber");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportDataSubscriberToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "DataSubscriber");
            }
        }

        protected SQLFlowUi.Models.sqlflowProd.DataSubscriber dataSubscriber;
        protected async Task GetChildData(SQLFlowUi.Models.sqlflowProd.DataSubscriber args)
        {
            dataSubscriber = args;
            var DataSubscriberQueryResult = await sqlflowProdService.GetDataSubscriberQuery(new Query { Filter = $@"i => i.FlowID == {args.FlowID}", Expand = "DataSubscriber" });
            if (DataSubscriberQueryResult != null)
            {
                args.DataSubscriberQuery = DataSubscriberQueryResult.ToList();
            }
        }

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery> DataSubscriberQueryDataGrid;

        protected async Task DataSubscriberQueryAddButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.DataSubscriber data)
        {
            var dialogResult = await DialogService.OpenAsync<AddDataSubscriberQuery>("Add DataSubscriberQuery", new Dictionary<string, object> { {"FlowID" , data.FlowID} });
            await GetChildData(data);
            await DataSubscriberQueryDataGrid.Reload();
        }

        protected async Task DataSubscriberQueryRowSelect(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery> args, SQLFlowUi.Models.sqlflowProd.DataSubscriber data)
        {
            var dialogResult = await DialogService.OpenAsync<EditDataSubscriberQuery>("Edit DataSubscriberQuery", new Dictionary<string, object> { {"QueryID", args.Data.QueryID} });
            await GetChildData(data);
            await DataSubscriberQueryDataGrid.Reload();
        }

        protected async Task DataSubscriberQueryDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery dataSubscriberQuery)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await sqlflowProdService.DeleteDataSubscriberQuery(dataSubscriberQuery.QueryID);

                    await GetChildData(dataSubscriber);

                    if (deleteResult != null)
                    {
                        await DataSubscriberQueryDataGrid.Reload();
                    }
                }
            }
            catch (System.Exception ex)
            {
                NotificationService.Notify(new NotificationMessage
                {
                    Severity = NotificationSeverity.Error,
                    Summary = $"Error",
                    Detail = $"Unable to delete DataSubscriberQuery"
                });
            }
        }
    }
}