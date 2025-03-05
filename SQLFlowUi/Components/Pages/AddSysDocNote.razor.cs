using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class AddSysDocNote
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
        
        [Parameter] public string ObjectName { get; set; } = "";
        
        protected override async Task OnInitializedAsync()
        {
            sysDocNote = new SQLFlowUi.Models.sqlflowProd.SysDocNote();
            sysDoc = await sqlflowProdService.GetSysDoc();
            sysFlowNoteType = await sqlflowProdService.GetSysFlowNoteTypes();


            if (ObjectName.Length > 0)
            {
                sysDocNote.ObjectName = ObjectName;
            }
            
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.SysDocNote sysDocNote;
        protected IEnumerable<SQLFlowUi.Models.sqlflowProd.SysDoc> sysDoc;

        protected IQueryable<SQLFlowUi.Models.sqlflowProd.SysFlowNoteType> sysFlowNoteType;
        
        protected async Task FormSubmit()
        {
            try
            {
                sysDocNote.CreatedBy = Security.User?.Name;
                sysDocNote.Created = DateTime.Now;
                await sqlflowProdService.CreateSysDocNote(sysDocNote);
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