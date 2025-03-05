using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class AddSysFlowNote
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

        [Parameter] public int FlowID { get; set; } = 0;
        
        protected override async Task OnInitializedAsync()
        {
            sysFlowNote = new SQLFlowUi.Models.sqlflowProd.SysFlowNote();
            flowDS = await sqlflowProdService.GetFlowDs();
            sysFlowNote.FlowID = FlowID;
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
                sysFlowNote.CreatedBy = Security.User?.Name;
                sysFlowNote.Created = DateTime.Now;
                sysFlowNote.Resolved = false;
                await sqlflowProdService.CreateSysFlowNote(sysFlowNote);
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