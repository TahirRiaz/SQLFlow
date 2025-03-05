using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class BatchExecutionChart
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
    }

    public class BatchData
    {
        public string Batch { get; set; }
        
        public DateTime StartTime { get; set; }
        public DateTime EndTime { get; set; }
    }

    

    public class DatasetItem
    {
        public string measure { get; set; }
        public string measure_html { get; set; }
        public string measure_url { get; set; }
        
        public List<List<object>> data { get; set; }
    }
}