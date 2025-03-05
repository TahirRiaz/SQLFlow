using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class EditInvoke
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

        [Parameter]
        public int FlowID { get; set; }
       
        protected override async Task OnInitializedAsync()
        {
            invoke = await sqlflowProdService.GetInvokeByFlowId(FlowID);
            sysServicePrincipal = await sqlflowProdService.GetSysServicePrincipal();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.Invoke invoke; 
        
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> sysServicePrincipal;
        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.UpdateInvoke(FlowID, invoke);
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