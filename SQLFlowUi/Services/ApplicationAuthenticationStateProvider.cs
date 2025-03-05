using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;

using SQLFlowUi.Models;

namespace SQLFlowUi
{
    public class ApplicationAuthenticationStateProvider : AuthenticationStateProvider
    {
        private readonly SecurityService securityService;
        private ApplicationAuthenticationState authenticationState;

        public ApplicationAuthenticationStateProvider(SecurityService securityService)
        {
            this.securityService = securityService;
        }

        public override async Task<AuthenticationState> GetAuthenticationStateAsync()
        {
            var identity = new ClaimsIdentity();

            try
            {
                var state = await GetApplicationAuthenticationStateAsync();

                if (state.IsAuthenticated)
                {
                    identity = new ClaimsIdentity(state.Claims.Select(c => new Claim(c.Type, c.Value)), "SQLFlowUi");
                }
            }
            catch (HttpRequestException ex)
            {
                Console.WriteLine(ex.ToString());
            }

            var result = new AuthenticationState(new ClaimsPrincipal(identity));
            //NotifyAuthenticationStateChanged(Task.FromResult(result));
            await securityService.InitializeAsync(result);

            return result;
        }

        private async Task<ApplicationAuthenticationState> GetApplicationAuthenticationStateAsync()
        {
            if (authenticationState == null)
            {
                authenticationState = await securityService.GetAuthenticationStateAsync();
            }

            return authenticationState;
        }
    }
}