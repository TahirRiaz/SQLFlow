using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class EditPreIngestionADOVirtual
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
        public int VirtualID { get; set; }

        protected override async Task OnInitializedAsync()
        {
            preIngestionADOVirtual = await sqlflowProdService.GetPreIngestionADOVirtualByVirtualId(VirtualID);
            flowDS = await sqlflowProdService.GetFlowDs();
            sysDataSources = await sqlflowProdService.GetSysDataSource();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.PreIngestionADOVirtual preIngestionADOVirtual;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.FlowDS> flowDS;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> sysDataSources;
        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.UpdatePreIngestionADOVirtual(VirtualID, preIngestionADOVirtual);
                DialogService.Close(preIngestionADOVirtual);
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