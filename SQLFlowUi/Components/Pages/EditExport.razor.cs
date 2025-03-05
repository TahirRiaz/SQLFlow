using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class EditExport
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
        public int FlowID { get; set; }

        protected override async Task OnInitializedAsync()
        {
            export = await sqlflowProdService.GetExportByFlowId(FlowID);
            sysExportBies = await sqlflowProdService.GetSysExportBy();
            sysSubFolderPatterns = await sqlflowProdService.GetSysSubFolderPatterns();
            sysCompressionTypes = await sqlflowProdService.GetSysCompressionType();
            sysFileEncodings = await sqlflowProdService.GetSysFileEncodings();
            invokes = await sqlflowProdService.GetInvoke();
            sysServicePrincipal = await sqlflowProdService.GetSysServicePrincipal();
            sysDataSources = await sqlflowProdService.GetSysDataSource();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.Export export;

        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> sysDataSources;

        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysExportBy> sysExportBies;

        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysSubFolderPattern> sysSubFolderPatterns;

        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysCompressionType> sysCompressionTypes;

        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysFileEncoding> sysFileEncodings;

        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.Invoke> invokes;

        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> sysServicePrincipal;

        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.UpdateExport(FlowID, export);
                DialogService.Close(export);
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