using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Newtonsoft.Json;

namespace SQLFlowUi.Components.Pages
{
    public partial class AddSysAIPrompts
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
        protected SQLFlowCore.ExecParams.OpenAIPayLoad openAIPayLoad = new SQLFlowCore.ExecParams.OpenAIPayLoad();

        
        protected override async Task OnInitializedAsync()
        {
            sysAIPrompt = new SQLFlowUi.Models.sqlflowProd.SysAIPrompt();
            sysAPIKey = await sqlflowProdService.GetSysAPIKey();
            
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.SysAIPrompt sysAIPrompt;
        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.SysAPIKey> sysAPIKey;
        protected async Task FormSubmit()
        {
            try
            {
                string payloadJson = JsonConvert.SerializeObject(openAIPayLoad, Formatting.Indented);
                sysAIPrompt.PayLoadJson = payloadJson;
                await sqlflowProdService.CreateSysAIPrompt(sysAIPrompt);
                DialogService.Close(sysAIPrompt);
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