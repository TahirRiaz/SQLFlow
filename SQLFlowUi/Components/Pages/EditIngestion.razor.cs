using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class EditIngestion
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
            ingestion = await sqlflowProdService.GetIngestionByFlowId(FlowID);
            sysDataSources = await sqlflowProdService.GetSysDataSource();
            invokes = await sqlflowProdService.GetInvoke();
            assertion = (await sqlflowProdService.GetAssertion()).OrderBy(a => a.AssertionName);;
            assertionList = ingestion.Assertions?.Split(',').ToList();
            sysColList = ingestion.SysColumns?.Split(',').ToList();
            sysColumns = await sqlflowProdService.GetSysColumn();
            sysExportBies = await sqlflowProdService.GetSysExportBy();
            sysHashKeyType = await sqlflowProdService.GetSysHashKeyType();

        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.Ingestion ingestion;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> sysDataSources;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.Invoke> invokes;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysExportBy> sysExportBies;

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.SysHashKeyType> sysHashKeyType;

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
                await sqlflowProdService.UpdateIngestion(FlowID, ingestion);
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