using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class EditLineageObjectMK
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
        public int ObjectMK { get; set; }

        protected override async Task OnInitializedAsync()
        {
            lineageObjectMK = await sqlflowProdService.GetLineageObjectMKByObjectMk(ObjectMK);
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.LineageObjectMK lineageObjectMK;

        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.UpdateLineageObjectMK(ObjectMK, lineageObjectMK);
                DialogService.Close(lineageObjectMK);
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
