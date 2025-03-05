using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;


namespace SQLFlowUi.Components.Pages
{
    public partial class AddDataSubscriber
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
            dataSubscriber = new SQLFlowUi.Models.sqlflowProd.DataSubscriber();
            sysDataSources = await sqlflowProdService.GetSysDataSource();
            dataSubscriberType = await sqlflowProdService.GetDataSubscriberType();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.DataSubscriber dataSubscriber;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSource> sysDataSources;

        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.SysDataSubscriberType> dataSubscriberType;

        protected async Task FormSubmit()
        {
            try
            {
                dataSubscriber.CreatedBy = Security.User?.Name;
                dataSubscriber.CreatedDate = DateTime.Now;
                await sqlflowProdService.CreateDataSubscriber(dataSubscriber);
                
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

        protected static int GenerateRandomInt(int min, int max)
        {
            Random random = new Random();
            return random.Next(min, max + 1);
        }
    }
}