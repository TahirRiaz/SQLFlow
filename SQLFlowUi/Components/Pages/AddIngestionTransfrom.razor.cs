using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class AddIngestionTransfrom
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

        protected override async Task OnInitializedAsync()
        {
            ingestionTransfrom = new SQLFlowUi.Models.sqlflowProd.IngestionTransfrom();
            flowDS = await sqlflowProdService.GetFlowDs();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.IngestionTransfrom ingestionTransfrom;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.FlowDS> flowDS;
        
        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.CreateIngestionTransfrom(ingestionTransfrom);
                DialogService.Close(ingestionTransfrom);
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