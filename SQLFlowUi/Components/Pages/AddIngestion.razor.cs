using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class AddIngestion
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
            ingestion = new SQLFlowUi.Models.sqlflowProd.Ingestion();
            sysDataSources = await sqlflowProdService.GetSysDataSource();
            invokes = await sqlflowProdService.GetInvoke();
            sysExportBies = await sqlflowProdService.GetSysExportBy();
            assertion = await sqlflowProdService.GetAssertion();
            sysHashKeyType = await sqlflowProdService.GetSysHashKeyType();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.Ingestion ingestion;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> sysDataSources;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.Invoke> invokes;
        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.SysHashKeyType> sysHashKeyType;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysExportBy> sysExportBies;
        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.Assertion> assertion;
        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.SysColumn> sysColumns;
        IList<string> assertionList = new string[] { };
        IList<string> sysColList = new string[] { };
        
        protected async Task FormSubmit()
        {
            try
            {
                ingestion.Assertions = string.Join(",", assertionList);
                ingestion.SysColumns = string.Join(",", sysColList);

                ingestion.CreatedBy = Security.User?.Name;
                ingestion.CreatedDate = DateTime.Now;
                await sqlflowProdService.CreateIngestion(ingestion);
                DialogService.Close(ingestion);
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