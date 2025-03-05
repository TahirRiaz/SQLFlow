using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class EditDataSubscriber
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
            dataSubscriber = await sqlflowProdService.GetDataSubscriberByFlowId(FlowID);
            dataSubscriberType = await sqlflowProdService.GetDataSubscriberType();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.DataSubscriber dataSubscriber;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSubscriberType> dataSubscriberType;

        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.UpdateDataSubscriber(FlowID, dataSubscriber);
                DialogService.Close(dataSubscriber);
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