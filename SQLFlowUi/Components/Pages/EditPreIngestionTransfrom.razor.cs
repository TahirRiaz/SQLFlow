using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class EditPreIngestionTransfrom
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
        public int TransfromID { get; set; }

        protected override async Task OnInitializedAsync()
        {
            preIngestionTransfrom = await sqlflowProdService.GetPreIngestionTransfromByTransfromId(TransfromID);
            flowDS = await sqlflowProdService.GetFlowDs();
            sysDataSources = await sqlflowProdService.GetSysDataSource();
            sysFlowType = await sqlflowProdService.GetSysFlowType();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.PreIngestionTransfrom preIngestionTransfrom;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.FlowDS> flowDS;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> sysDataSources;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysFlowType> sysFlowType;
        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.UpdatePreIngestionTransfrom(TransfromID, preIngestionTransfrom);
                DialogService.Close(preIngestionTransfrom);
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