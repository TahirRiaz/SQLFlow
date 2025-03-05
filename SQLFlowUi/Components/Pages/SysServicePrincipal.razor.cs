using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class SysServicePrincipal
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

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> sysServicePrincipal;

        protected RadzenDataGrid<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> grid0;

        protected string search = "";

        protected async Task Search(ChangeEventArgs args)
        {
            search = $"{args.Value}";

            await grid0.GoToPage(0);

            sysServicePrincipal = await sqlflowProdService.GetSysServicePrincipal(new Query { Filter = $@"i => i.ServicePrincipalAlias.Contains(@0) || i.TenantId.Contains(@0) || i.SubscriptionId.Contains(@0) || i.ApplicationId.Contains(@0) || i.ClientSecret.Contains(@0)  || i.ResourceGroup.Contains(@0) || i.DataFactoryName.Contains(@0) || i.AutomationAccountName.Contains(@0) || i.BlobContainer.Contains(@0) || i.KeyVaultName.Contains(@0)", FilterParameters = new object[] { search } });
        }
        protected override async Task OnInitializedAsync()
        {
            sysServicePrincipal = await sqlflowProdService.GetSysServicePrincipal(new Query { Filter = $@"i => i.ServicePrincipalAlias.Contains(@0) || i.TenantId.Contains(@0) || i.SubscriptionId.Contains(@0) || i.ApplicationId.Contains(@0) || i.ClientSecret.Contains(@0)  || i.ResourceGroup.Contains(@0) || i.DataFactoryName.Contains(@0) || i.AutomationAccountName.Contains(@0) || i.BlobContainer.Contains(@0) || i.KeyVaultName.Contains(@0)", FilterParameters = new object[] { search } });
        }

        protected async Task AddButtonClick(MouseEventArgs args)
        {
            await DialogService.OpenAsync<AddSysServicePrincipal>("Add SysServicePrincipal", null, GlobalSettings.EditOptions);
            await grid0.Reload();
        }

        protected async Task EditRow(DataGridRowMouseEventArgs<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> args)
        {
            await DialogService.OpenAsync<EditSysServicePrincipal>("Edit SysServicePrincipal", new Dictionary<string, object> { {"ServicePrincipalID", args.Data.ServicePrincipalID} }, GlobalSettings.EditOptions);
        }

        protected async Task GridDeleteButtonClick(MouseEventArgs args, SQLFlowUi.Models.sqlflowProd.SysServicePrincipal sysServicePrincipal)
        {
            try
            {
                if (await DialogService.Confirm("Are you sure you want to delete this record?") == true)
                {
                    var deleteResult = await sqlflowProdService.DeleteSysServicePrincipal(sysServicePrincipal.ServicePrincipalID);

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
                    Detail = $"Unable to delete SysServicePrincipal"
                });
            }
        }

        protected async Task ExportClick(RadzenSplitButtonItem args)
        {
            if (args?.Value == "csv")
            {
                await sqlflowProdService.ExportSysServicePrincipalToCSV(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "SysServicePrincipal");
            }

            if (args == null || args.Value == "xlsx")
            {
                await sqlflowProdService.ExportSysServicePrincipalToExcel(new Query
{
    Filter = $@"{(string.IsNullOrEmpty(grid0.Query.Filter)? "true" : grid0.Query.Filter)}",
    OrderBy = $"{grid0.Query.OrderBy}",
    Expand = "",
    Select = string.Join(",", grid0.ColumnsCollection.Where(c => c.GetVisible() && !string.IsNullOrEmpty(c.Property)).Select(c => c.Property.Contains(".") ? c.Property + " as " + c.Property.Replace(".", "") : c.Property))
}, "SysServicePrincipal");
            }
        }
    }
}