using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class AddIngestionTokenize
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
            ingestionTokenize = new SQLFlowUi.Models.sqlflowProd.IngestionTokenize();
            flowDS = await sqlflowProdService.GetFlowDs();
            sysDataSources = await sqlflowProdService.GetSysDataSource();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.IngestionTokenize ingestionTokenize;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.FlowDS> flowDS;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> sysDataSources;

        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.CreateIngestionTokenize(ingestionTokenize);
                DialogService.Close(ingestionTokenize);
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