using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class AddPreIngestionADO
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
        [Inject]
        protected SecurityService Security { get; set; }

        protected override async Task OnInitializedAsync()
        {
            sysExportBies = await sqlflowProdService.GetSysExportBy();
            preIngestionADO = new SQLFlowUi.Models.sqlflowProd.PreIngestionADO();
            sysDataSources = await sqlflowProdService.GetSysDataSource();
            invokes = await sqlflowProdService.GetInvoke();
        }
        protected bool errorVisible;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysExportBy> sysExportBies;
        protected SQLFlowUi.Models.sqlflowProd.PreIngestionADO preIngestionADO;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> sysDataSources;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.Invoke> invokes;
        protected async Task FormSubmit()
        {
            try
            {
                preIngestionADO.CreatedBy = Security.User?.Name;
                preIngestionADO.CreatedDate = DateTime.Now;
                await sqlflowProdService.CreatePreIngestionADO(preIngestionADO);
                DialogService.Close(preIngestionADO);
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