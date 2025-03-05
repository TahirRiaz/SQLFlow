using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class EditSysHashKeyType
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
        public string HashKeyType { get; set; }

        protected override async Task OnInitializedAsync()
        {
            sysHashKeyType = await sqlflowProdService.GetSysHashKeyTypeByHashKeyType(HashKeyType);
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.SysHashKeyType sysHashKeyType;

        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.UpdateSysHashKeyType(HashKeyType, sysHashKeyType);
                DialogService.Close(sysHashKeyType);
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