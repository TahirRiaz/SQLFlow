using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class AddPreIngestionCSV
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
            preIngestionCSV = new SQLFlowUi.Models.sqlflowProd.PreIngestionCSV();
            sysDataSources = await sqlflowProdService.GetSysDataSource();
            sysServicePrincipal = await sqlflowProdService.GetSysServicePrincipal();
            invokes = await sqlflowProdService.GetInvoke();
            SysFileEncoding = await sqlflowProdService.GetSysFileEncodings();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.PreIngestionCSV preIngestionCSV;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> sysDataSources;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysServicePrincipal> sysServicePrincipal;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.Invoke> invokes;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysFileEncoding> SysFileEncoding;

        protected async Task FormSubmit()
        {
            try
            {
                preIngestionCSV.CreatedBy = Security.User?.Name;
                preIngestionCSV.CreatedDate = DateTime.Now;
                await sqlflowProdService.CreatePreIngestionCSV(preIngestionCSV);
                DialogService.Close(preIngestionCSV);
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