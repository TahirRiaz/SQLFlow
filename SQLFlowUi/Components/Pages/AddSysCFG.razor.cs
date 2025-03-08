using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using SQLFlowUi.Data.MetaData;

namespace SQLFlowUi.Components.Pages
{
    public partial class AddSysCFG
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

        private JsonGuiModel MyJsonValue { get; set; }
        
        protected override async Task OnInitializedAsync()
        {
            sysCFG = new SQLFlowUi.Models.sqlflowProd.SysCFG();
            MyJsonValue = new JsonGuiModel();
            //MyJsonValue.JsonValue = "";
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.SysCFG sysCFG;

        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.CreateSysCFG(sysCFG);
                DialogService.Close(sysCFG);
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