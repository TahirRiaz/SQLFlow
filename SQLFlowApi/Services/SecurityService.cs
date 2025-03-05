using System.Security.Claims;
using SQLFlowApi.Models;
using Microsoft.AspNetCore.Components.Authorization;
using SQLFlowApi.Services.Extention;

namespace SQLFlowApi
{
    public partial class SecurityService
    {
        private readonly HttpClient httpClient;
        public ApplicationUser User { get; private set; } = new ApplicationUser { Name = "Anonymous" };
        public ClaimsPrincipal Principal { get; private set; }

        public SecurityService(IHttpClientFactory factory)
        {
            this.httpClient = factory.CreateClient("SQLFlowUi");
        }

        public bool IsInRole(params string[] roles)
        {
#if DEBUG
            if (User.Name == "admin")
            {
                return true;
            }
#endif

            if (roles.Contains("Everybody"))
            {
                return true;
            }

            if (!IsAuthenticated())
            {
                return false;
            }

            if (roles.Contains("Authenticated"))
            {
                return true;
            }

            return roles.Any(role => Principal.IsInRole(role));
        }

        public bool IsAuthenticated()
        {
            return Principal?.Identity.IsAuthenticated == true;
        }

        public async Task<bool> InitializeAsync(AuthenticationState result)
        {
            Principal = result.User;
#if DEBUG
            if (Principal.Identity.Name == "admin")
            {
                User = new ApplicationUser { Name = "Admin" };
                return true;
            }
#endif
            var userId = Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

            if (userId != null && User?.Id != userId)
            {
                User = await GetUserById(userId);
            }

            return IsAuthenticated();
        }

        public async Task<ApplicationUser> GetUserById(string id)
        {
            var uri = new Uri($"odata/Identity/ApplicationUsers('{id}')?$expand=Roles"); // Modify this line accordingly if needed

            var response = await httpClient.GetAsync(uri);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            return await HttpResponseMessageExtensions.ReadAsync<ApplicationUser>(response);
        }

        public async Task<ApplicationAuthenticationState> GetAuthenticationStateAsync()
        {
            var uri = new Uri($"Account/CurrentUser"); // Modify this line accordingly if needed

            var response = await httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, uri));

            return await HttpResponseMessageExtensions.ReadAsync<ApplicationAuthenticationState>(response);
        }

        // Note: The Logout and Login methods have been removed as they depended on NavigationManager
    }
}
