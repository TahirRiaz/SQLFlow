using Microsoft.JSInterop;
using Microsoft.AspNetCore.Components;
using Radzen;

namespace SQLFlowUi.Components.Pages
{
    public partial class Login
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

        protected string redirectUrl;
        protected string error;
        protected string info;
        protected bool errorVisible;
        protected bool infoVisible;

        [Inject]
        protected SecurityService Security { get; set; }

        protected override async Task OnInitializedAsync()
        {
            var query = System.Web.HttpUtility.ParseQueryString(new Uri(NavigationManager.ToAbsoluteUri(NavigationManager.Uri).ToString()).Query);

            error = query.Get("error");

            info = query.Get("info");

            redirectUrl = query.Get("redirectUrl");

            errorVisible = !string.IsNullOrEmpty(error);

            infoVisible = !string.IsNullOrEmpty(info);
        }

        protected async Task ResetPassword()
        {
            var result = await DialogService.OpenAsync<ResetPassword>("Reset password");

            if (result == true)
            {
                infoVisible = true;

                info = "Password reset successfully. Please check your email for further instructions.";
            }
        }
    }
}