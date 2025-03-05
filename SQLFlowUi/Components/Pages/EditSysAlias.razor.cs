using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class EditSysAlias
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
        public int SystemID { get; set; }

        protected override async Task OnInitializedAsync()
        {
            sysAlias = await sqlflowProdService.GetSysAliasBySystemId(SystemID);
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.SysAlias sysAlias;

        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.UpdateSysAlias(SystemID, sysAlias);
                DialogService.Close(sysAlias);
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