using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class AddDataSubscriberQuery
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

            dataSubscriberForFlowID = await sqlflowProdService.GetDataSubscriber();
            sysDataSources = await sqlflowProdService.GetSysDataSource();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery dataSubscriberQuery;

        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.DataSubscriber> dataSubscriberForFlowID;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> sysDataSources;
        
        
        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.CreateDataSubscriberQuery(dataSubscriberQuery);
                DialogService.Close(dataSubscriberQuery);
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





        bool hasFlowIDValue;

        [Parameter]
        public int FlowID { get; set; }
        public override async Task SetParametersAsync(ParameterView parameters)
        {
            dataSubscriberQuery = new SQLFlowUi.Models.sqlflowProd.DataSubscriberQuery();

            hasFlowIDValue = parameters.TryGetValue<int>("FlowID", out var hasFlowIDResult);

            if (hasFlowIDValue)
            {
                dataSubscriberQuery.FlowID = hasFlowIDResult;
            }
            await base.SetParametersAsync(parameters);
        }
    }
}