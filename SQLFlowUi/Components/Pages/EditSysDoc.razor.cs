using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class EditSysDoc
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
        public int SysDocID { get; set; }

        protected override async Task OnInitializedAsync()
        {
            sysDoc = await sqlflowProdService.GetSysDocBySysDocId(SysDocID);
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.SysDoc sysDoc;

        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.UpdateSysDoc(SysDocID, sysDoc);
                DialogService.Close(sysDoc);
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