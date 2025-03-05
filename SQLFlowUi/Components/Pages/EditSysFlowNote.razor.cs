using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class EditSysFlowNote
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
        public int FlowNoteID { get; set; }

        protected override async Task OnInitializedAsync()
        {
            sysFlowNote = await sqlflowProdService.GetSysFlowNoteByFlowNoteId(FlowNoteID);
            flowDS = await sqlflowProdService.GetFlowDs();
            sysFlowNoteType = await sqlflowProdService.GetSysFlowNoteTypes();
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.SysFlowNote sysFlowNote;
        protected System.Linq.IQueryable<SQLFlowUi.Models.sqlflowProd.FlowDS> flowDS;
        protected IQueryable<SQLFlowUi.Models.sqlflowProd.SysFlowNoteType> sysFlowNoteType;
        
        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.UpdateSysFlowNote(FlowNoteID, sysFlowNote);
                DialogService.Close(sysFlowNote);
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