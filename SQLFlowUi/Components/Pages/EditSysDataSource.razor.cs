using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class EditSysDataSource
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
        public int DataSourceID { get; set; }

        protected override async Task OnInitializedAsync()
        {
            sysDataSource = await sqlflowProdService.GetSysDataSourceByDataSourceId(DataSourceID);
            sysServicePrincipal = await sqlflowProdService.GetSysServicePrincipal();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.SysDataSource sysDataSource;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> sysServicePrincipal;

        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.UpdateSysDataSource(DataSourceID, sysDataSource);
                DialogService.Close(sysDataSource);
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