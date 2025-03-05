using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;


namespace SQLFlowUi.Components.Pages
{
    public partial class AddStoredProcedure
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
            storedProcedure = new SQLFlowUi.Models.sqlflowProd.StoredProcedure();
            sysDataSources = await sqlflowProdService.GetSysDataSource();
            sysServicePrincipal = await sqlflowProdService.GetSysServicePrincipal();
            invokes = await sqlflowProdService.GetInvoke();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.StoredProcedure storedProcedure;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> sysDataSources;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> sysServicePrincipal;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.Invoke> invokes;

        protected async Task FormSubmit()
        {
            try
            {
                storedProcedure.CreatedBy = Security.User?.Name;
                storedProcedure.CreatedDate = DateTime.Now;
                await sqlflowProdService.CreateStoredProcedure(storedProcedure);
                DialogService.Close(storedProcedure);
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