using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Radzen;
using Radzen.Blazor;

namespace SQLFlowUi.Components.Pages
{
    public partial class EditSysLogMatchKey
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
        public int SysLogMatchKey1 { get; set; }

        protected override async Task OnInitializedAsync()
        {
            sysLogMatchKey = await sqlflowProdService.GetSysLogMatchKeyBySysLogMatchKey1(SysLogMatchKey1);
        }
        protected bool errorVisible;
        protected SQLFlowUi.Models.sqlflowProd.SysLogMatchKey sysLogMatchKey;

        [Inject]
        protected SecurityService Security { get; set; }

        protected async Task FormSubmit()
        {
            try
            {
                await sqlflowProdService.UpdateSysLogMatchKey(SysLogMatchKey1, sysLogMatchKey);
                DialogService.Close(sysLogMatchKey);
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