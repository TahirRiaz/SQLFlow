using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class AddExport
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
            export = new SQLFlowUi.Models.sqlflowProd.Export();
            sysExportBies = await sqlflowProdService.GetSysExportBy();
            sysSubFolderPatterns = await sqlflowProdService.GetSysSubFolderPatterns();
            sysCompressionTypes = await sqlflowProdService.GetSysCompressionType();
            sysFileEncodings = await sqlflowProdService.GetSysFileEncodings();
            invokes = await sqlflowProdService.GetInvoke();
            sysDataSources = await sqlflowProdService.GetSysDataSource();
            sysServicePrincipal = await sqlflowProdService.GetSysServicePrincipal();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.Export export;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysExportBy> sysExportBies;

        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysSubFolderPattern> sysSubFolderPatterns;

        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysCompressionType> sysCompressionTypes;

        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysFileEncoding> sysFileEncodings;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.Invoke> invokes;

        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> sysDataSources;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> sysServicePrincipal;

        protected async Task FormSubmit()
        {
            try
            {
                export.CreatedBy = Security.User?.Name;
                export.CreatedDate = DateTime.Now;
                await sqlflowProdService.CreateExport(export);
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