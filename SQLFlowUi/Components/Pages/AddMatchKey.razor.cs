using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class AddMatchKey
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
            matchKey = new SQLFlowUi.Models.sqlflowProd.MatchKey();
            sysDataSources = await sqlflowProdService.GetSysDataSource();
            flowDS = await sqlflowProdService.GetFlowDs();
            sysMatchKeyDeletedRowHandeling = await sqlflowProdService.GetSysMatchKeyDeletedRowHandeling();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.MatchKey matchKey;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> sysDataSources;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.FlowDS> flowDS;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysMatchKeyDeletedRowHandeling> sysMatchKeyDeletedRowHandeling;

        protected async Task FormSubmit()
        {
            try
            {
                matchKey.CreatedBy = Security.User?.Name;
                matchKey.CreatedDate = DateTime.Now;

                
                
                await sqlflowProdService.CreateMatchKey(matchKey);
                DialogService.Close(matchKey);
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