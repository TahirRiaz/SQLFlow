using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class Search
    {
        private int currentKey = 0;

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
    

}