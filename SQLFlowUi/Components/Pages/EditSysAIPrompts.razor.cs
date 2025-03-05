using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Newtonsoft.Json;

namespace SQLFlowUi.Components.Pages
{
    public partial class EditSysAIPrompts
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
        public int PromptID { get; set; }

        protected override async Task OnInitializedAsync()
        {
            openAIPayLoad = new SQLFlowCore.ExecParams.OpenAIPayLoad();
            sysAIPrompt = await sqlflowProdService.GetSysAIPromptByPromptId(PromptID);
            sysAPIKey = await sqlflowProdService.GetSysAPIKey();

            if (sysAIPrompt.PayLoadJson?.Length > 0 )
            {
                try
                {
                    openAIPayLoad = JsonConvert.DeserializeObject<SQLFlowCore.ExecParams.OpenAIPayLoad>(sysAIPrompt.PayLoadJson);
                }
                catch
                {
                    
                }
            }
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.SysAIPrompt sysAIPrompt;
        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.SysAPIKey> sysAPIKey;
        public override string ToString()
        {
            return base.ToString();
        }

        SQLFlowCore.ExecParams.OpenAIPayLoad openAIPayLoad = new SQLFlowCore.ExecParams.OpenAIPayLoad();
        
        protected async Task FormSubmit()
        {
            try
            {
                string payloadJson = JsonConvert.SerializeObject(openAIPayLoad, Formatting.Indented);
                sysAIPrompt.PayLoadJson = payloadJson;
                await sqlflowProdService.UpdateSysAIPrompt(PromptID, sysAIPrompt);
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