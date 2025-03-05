using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class AddSysSourceControl
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
            sysSourceControl = new SQLFlowUi.Models.sqlflowProd.SysSourceControl();
            sysDataSources = await sqlflowProdService.GetSysDataSource();
            sourceControlTypes = await sqlflowProdService.GetSysSourceControlType();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.SysSourceControl sysSourceControl;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> sysDataSources;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysSourceControlType> sourceControlTypes;

        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.CreateSysSourceControl(sysSourceControl);
                DialogService.Close(sysSourceControl);
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