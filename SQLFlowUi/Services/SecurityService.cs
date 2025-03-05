using System.Text;
using System.Security.Claims;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using SQLFlowUi.Models;
using SQLFlowUi.Service;
using SQLFlowUi.Services;
using Microsoft.IdentityModel.Tokens;
using System.IdentityModel.Tokens.Jwt;

namespace SQLFlowUi
{
    public partial class SecurityService
    {
        private readonly HttpClient httpClient;

        private readonly Uri baseUri;
        private readonly NavigationManager navigationManager;
        private readonly ConfigService configService;
        private readonly IHttpService httpService;
        private readonly SignInManager<ApplicationUser> signInManager;
        private readonly UserManager<ApplicationUser> userManager;
        private readonly IHttpContextAccessor httpContextAccessor;
        public ApplicationUser User { get; private set; } = new ApplicationUser { Name = "Anonymous" };

        public ClaimsPrincipal Principal { get; private set; }

        public SecurityService(IHttpContextAccessor httpContextAccessor, SignInManager<ApplicationUser> signInManager, NavigationManager navigationManager, UserManager<ApplicationUser> userManager, IHttpClientFactory factory, ConfigService configService, IHttpService httpService, IUserInformationService userInformationService) //, IHttpService HttpService
        {
            this.baseUri = new Uri($"{navigationManager.BaseUri}odata/Identity/");
            this.httpClient = factory.CreateClient("SQLFlowUi");
            this.navigationManager = navigationManager;
            this.configService = configService;
            this.httpService = httpService;
            this.signInManager = signInManager;
            this.httpContextAccessor = httpContextAccessor;
            this.userManager = userManager;

            
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

            var userId = Principal?.FindFirstValue(ClaimTypes.NameIdentifier);

            if (userId != null && User?.Id != userId)
            {
                User = await GetUserById(userId);
            }

            return IsAuthenticated();
        }

        public async Task<ApplicationAuthenticationState> GetAuthenticationStateAsync()
        {
            var authenticationState = await GetCurrentUser();
            return authenticationState;

            //return await httpService.GetAuthenticationStateAsync();

            /*
                var uri = new Uri($"{navigationManager.BaseUri}Account/CurrentUser");

                var response = await httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, uri));

                return await response.ReadAsync<ApplicationAuthenticationState>();
            */
        }

        private async Task<ApplicationAuthenticationState> GetCurrentUser()
        {
            var user = await signInManager.UserManager.GetUserAsync(httpContextAccessor.HttpContext?.User);

            if (user != null)
            {
                var principal = await signInManager.CreateUserPrincipalAsync(user);

                var authenticationState = new ApplicationAuthenticationState
                {
                    IsAuthenticated = principal.Identity.IsAuthenticated,
                    Name = principal.Identity.Name,
                    Claims = principal.Claims.Select(c => new ApplicationClaim { Type = c.Type, Value = c.Value })
                };

                return authenticationState;
            }

            return new ApplicationAuthenticationState(); ;
        }

        public void Logout()
        {
            navigationManager.NavigateTo("Account/Logout", true);
        }

        public void Login()
        {
            navigationManager.NavigateTo("Login", true);
        }

        public async Task<IEnumerable<ApplicationRole>> GetRoles()
        {
            return await httpService.GetRolesAsync();
            
            /*
            var uri = new Uri(baseUri, $"ApplicationRoles");

            uri = uri.GetODataUri();

            var response = await httpClient.GetAsync(uri);

            var result = await response.ReadAsync<ODataServiceResult<ApplicationRole>>();

            return result.Value;
            */
        }

        public async Task<ApplicationRole> CreateRole(ApplicationRole role)
        {
            return await httpService.CreateRoleAsync(role);
            
            /*
            var uri = new Uri(baseUri, $"ApplicationRoles");
            var content = new StringContent(ODataJsonSerializer.Serialize(role), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(uri, content);
            return await response.ReadAsync<ApplicationRole>();
            */
        }

        public async Task<HttpResponseMessage> DeleteRole(string id)
        {
            return await httpService.DeleteRoleAsync(id);
            /*
                var uri = new Uri(baseUri, $"ApplicationRoles('{id}')");
                return await httpClient.DeleteAsync(uri);
            */
        }

        public async Task<IEnumerable<ApplicationUser>> GetUsers()
        {

            return await httpService.GetUsersAsync();

            /*
            var uri = new Uri(baseUri, $"ApplicationUsers");

            uri = uri.GetODataUri();

            var response = await httpClient.GetAsync(uri);
            var result = await response.ReadAsync<ODataServiceResult<ApplicationUser>>();

            return result.Value;
            */
        }

        public async Task<ApplicationUser> CreateUser(ApplicationUser user)
        {
            return await httpService.CreateUserAsync(user);
            
            /*
                var uri = new Uri(baseUri, $"ApplicationUsers");
                var content = new StringContent(JsonSerializer.Serialize(user), Encoding.UTF8, "application/json");
                var response = await httpClient.PostAsync(uri, content);
                return await response.ReadAsync<ApplicationUser>();
            */
        }

        public async Task<HttpResponseMessage> DeleteUser(string id)
        {
            return await httpService.DeleteUserAsync(id);
            /*
                var uri = new Uri(baseUri, $"ApplicationUsers('{id}')");
                return await httpClient.DeleteAsync(uri);
            */
        }

        public async Task<ApplicationUser> GetUserById(string id)
        {
            return await httpService.GetUserByIdAsync(id);
            /*
            var uri = new Uri(baseUri, $"ApplicationUsers('{id}')?$expand=Roles");

            var response = await httpClient.GetAsync(uri);

            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return null;
            }

            return await response.ReadAsync<ApplicationUser>();
            */
        }

        public async Task<ApplicationUser> UpdateUser(string id, ApplicationUser user)
        {
            return await httpService.UpdateUserAsync(id, user);

            /*

            var uri = new Uri(baseUri, $"ApplicationUsers('{id}')");

            var httpRequestMessage = new HttpRequestMessage(HttpMethod.Patch, uri)
            {
                Content = new StringContent(JsonSerializer.Serialize(user), Encoding.UTF8, "application/json")
            };

            var response = await httpClient.SendAsync(httpRequestMessage);

            return await response.ReadAsync<ApplicationUser>();
            */
        }
        public async Task ChangePassword(string oldPassword, string newPassword)
        {
            await httpService.ChangePasswordAsync(oldPassword, newPassword);
            /*
            var uri = new Uri($"{navigationManager.BaseUri}Account/ChangePassword");

            var content = new FormUrlEncodedContent(new Dictionary<string, string> {
                { "oldPassword", oldPassword },
                { "newPassword", newPassword }
            });

            var response = await httpClient.PostAsync(uri, content);

            if (!response.IsSuccessStatusCode)
            {
                var message = await response.Content.ReadAsStringAsync();

                throw new ApplicationException(message);
            }
            */
        }

        public async Task ResetPassword(string userName)
        {
            await httpService.ResetPasswordAsync(userName);
            /*
            var uri = new Uri($"{navigationManager.BaseUri}Account/ResetPassword");

            var content = new FormUrlEncodedContent(new Dictionary<string, string> {
                { "userName", userName }
            });

            var response = await httpClient.PostAsync(uri, content);

            if (!response.IsSuccessStatusCode)
            {
                var message = await response.Content.ReadAsStringAsync();

                throw new ApplicationException(message);
            }
            */
        }


        public async Task<string> GetJwtTokenAsync()
        {
            var user = await userManager.GetUserAsync(httpContextAccessor.HttpContext.User);

            var claims = new List<Claim>
            {
                new Claim(ClaimTypes.Name, user.UserName),
                new Claim(ClaimTypes.Email, user.Email),
                new Claim(ClaimTypes.NameIdentifier, user.Id),
            };

            var userRoles = await userManager.GetRolesAsync(user);
            claims.AddRange(userRoles.Select(role => new Claim(ClaimTypes.Role, role)));

            foreach (var claim in claims)
            {
                // Check if the user already has the claim
                var existingClaim = (await userManager.GetClaimsAsync(user))
                    .FirstOrDefault(c => c.Type == claim.Type && c.Value == claim.Value);

                if (existingClaim == null)
                {
                    // Claim does not exist, add it
                    await userManager.AddClaimAsync(user, claim);
                }
            }

            var keyBytes = Encoding.UTF8.GetBytes(configService.configSettings.SecretKey);
            var key = new SymmetricSecurityKey(keyBytes);
            var credentials = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);

            var expiryDuration = Convert.ToDouble(configService.configSettings.ExpireMinutes);
            var tokenDescriptor = new SecurityTokenDescriptor
            {
                Subject = new ClaimsIdentity(claims),
                Expires = DateTime.UtcNow.AddMinutes(expiryDuration),
                SigningCredentials = credentials,
                Issuer = configService.configSettings.Issuer,
                Audience = configService.configSettings.Audience
            };

            var tokenHandler = new JwtSecurityTokenHandler();
            var token = tokenHandler.CreateToken(tokenDescriptor);
            return tokenHandler.WriteToken(token);

            /*
            var payload = new
            {
                username = configService.configSettings.JwtAuthUserName,
                password = configService.configSettings.JwtAuthUserPwd
            };

            var jsonPayload = JsonConvert.SerializeObject(payload);
            var content = new StringContent(jsonPayload, Encoding.UTF8, "application/json");

            try
            {
                string _jwtAuthUrl;
                HttpClient _httpClient = new HttpClient();

                var response = await _httpClient.PostAsync(configService.configSettings.JwtAuthUrl, content);

                if (response.IsSuccessStatusCode)
                {
                    var token = await response.Content.ReadAsStringAsync();
                    return token; // Return the JWT token received from the server
                }
                else
                {
                    // Log or handle error
                    var error = await response.Content.ReadAsStringAsync();
                    throw new ApplicationException($"Failed to retrieve JWT token: {error}");
                }
            }
            catch (Exception ex)
            {
                // Log or handle exception
                throw new ApplicationException("Error while requesting JWT token", ex);
            }
            */
            
        }
    }
}