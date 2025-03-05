using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class EditLineageObjectRelation
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
        public int ObjectRelationID { get; set; }

        protected override async Task OnInitializedAsync()
        {
            lineageObjectRelation = await sqlflowProdService.GetLineageObjectRelationByObjectRelationId(ObjectRelationID);
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.LineageObjectRelation lineageObjectRelation;

        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.UpdateLineageObjectRelation(ObjectRelationID, lineageObjectRelation);
                DialogService.Close(lineageObjectRelation);
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