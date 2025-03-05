using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Radzen;
using SQLFlowCore.Services.UniqueKeyDetector;


namespace SQLFlowUi.Components.Pages
{
    public partial class DetectUniqueKey
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
        protected SQLFlowUi.sqlflowProdService sqlflowProdService { get; set; }

        [Inject]
        IConfiguration Configuration { get; set; }

        [Parameter]
        public int FlowID { get; set; }

        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.FlowDS> flowDS;
        
        private IEnumerable<ObjectColumns> objectColumn = new List<ObjectColumns>();
        private IEnumerable<string> SelectedValues = new string[] { };
        private SQLFlowUi.Models.sqlflowProd.SysDetectUniqueKey singleSysDetectUniqueKey = new SQLFlowUi.Models.sqlflowProd.SysDetectUniqueKey();// A single instance
        private IEnumerable<KeyDetectorResult> keyDetectorResults = new List<KeyDetectorResult>();
        protected override async Task OnInitializedAsync()
        {
            var sysDetectUniqueKeys = await sqlflowProdService.GetSysDetectUniqueKey();
            singleSysDetectUniqueKey = sysDetectUniqueKeys.FirstOrDefault();
            flowDS = await sqlflowProdService.GetFlowDs();

            flowDS = flowDS.Where(x => x.trgServer.Length > 0);

            //ColumnName
        }

        protected override async Task OnParametersSetAsync()
        {
            if (FlowID > 0 && flowId != FlowID) // Check if FlowID has changed
            {
                flowId = FlowID;
                await GetTrgColumns(flowId.ToString());
            }
        }

        protected override async Task OnAfterRenderAsync(bool firstRender)
        {
            if (firstRender)
            {
                
                
            }
        }
    }
}