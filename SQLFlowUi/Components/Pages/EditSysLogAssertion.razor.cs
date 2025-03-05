using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class EditSysLogAssertion
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
        public sqlflowProdService SqlflowProdService { get; set; }

        [Parameter]
        public int RecID { get; set; }

        protected override async Task OnInitializedAsync()
        {
            sysLogAssertion = await SqlflowProdService.GetSysLogAssertionByRecId(RecID);
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.SysLogAssertion sysLogAssertion;

        protected async Task FormSubmit()
        {
            try
            {
                await SqlflowProdService.UpdateSysLogAssertion(RecID, sysLogAssertion);
                DialogService.Close(sysLogAssertion);
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