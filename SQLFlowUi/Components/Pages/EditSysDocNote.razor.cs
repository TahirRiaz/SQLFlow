using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class EditSysDocNote
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
        public int DocNoteID { get; set; }

        protected override async Task OnInitializedAsync()
        {
            sysDocNote = await sqlflowProdService.GetSysDocNoteByDocNoteId(DocNoteID);
            sysDoc = await sqlflowProdService.GetSysDoc();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.SysDocNote sysDocNote;
        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.SysDoc> sysDoc;
        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.UpdateSysDocNote(DocNoteID, sysDocNote);
                DialogService.Close(sysDocNote);
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