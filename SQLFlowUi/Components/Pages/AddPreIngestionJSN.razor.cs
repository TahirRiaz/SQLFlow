using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class AddPreIngestionJSN
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
        protected SecurityService Security { get; set; }

        protected override async Task OnInitializedAsync()
        {
            preIngestionJSN = new SQLFlowUi.Models.sqlflowProd.PreIngestionJSN();
            sysDataSources = await sqlflowProdService.GetSysDataSource();
            sysServicePrincipal = await sqlflowProdService.GetSysServicePrincipal();
            invokes = await sqlflowProdService.GetInvoke();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.PreIngestionJSN preIngestionJSN;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> sysDataSources;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> sysServicePrincipal;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.Invoke> invokes;
        protected async Task FormSubmit()
        {
            try
            {
                preIngestionJSN.CreatedBy = Security.User?.Name;
                preIngestionJSN.CreatedDate = DateTime.Now;
                await sqlflowProdService.CreatePreIngestionJSN(preIngestionJSN);
                DialogService.Close(preIngestionJSN);
            }
            catch (Exception ex)
            {
                errorVisible = true;
            }
        }

        protected async Task CancelButtonClick(MouseEventArgs args)
        {
            DialogService.Close(null);
        }
    }
}