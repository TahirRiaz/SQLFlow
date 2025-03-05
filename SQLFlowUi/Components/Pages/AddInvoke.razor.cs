using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class AddInvoke
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
            invoke = new SQLFlowUi.Models.sqlflowProd.Invoke();
            sysServicePrincipal = await sqlflowProdService.GetSysServicePrincipal();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.Invoke invoke;
        
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> sysServicePrincipal;

        protected async Task FormSubmit()
        {
            try
            {
                //invoke.FlowID = DBNull.Value;
                invoke.CreatedBy = Security.User?.Name;
                invoke.CreatedDate = DateTime.Now;
                await sqlflowProdService.CreateInvoke(invoke);
                DialogService.Close(invoke);
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