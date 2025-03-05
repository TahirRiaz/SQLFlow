// For Encoding
// If using System.Text.Json for serialization
using Microsoft.AspNetCore.Components; // For NavigationManager
using SQLFlowUi.Models; // Assuming models such as ApplicationRole, ApplicationUser are defined here
using SQLFlowUi.Service; // If ConfigService is part of your service layer
using Radzen;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using System.Net;

namespace SQLFlowUi.Services
{
    public class HttpService : IHttpService, IUserInformationService
    {
        private readonly HttpClient httpClient;
        private readonly Uri baseUri;
        private readonly NavigationManager navigationManager;
        private readonly RoleManager<ApplicationRole> roleManager;
        private readonly UserManager<ApplicationUser> userManager;
        private readonly ConfigService configService;
        private readonly IHttpContextAccessor httpContextAccessor;
        //private readonly AuthenticationStateProvider authenticationStateProvider;
        
       
        
        public HttpService(IHttpClientFactory factory, NavigationManager navigationManager, IHttpContextAccessor httpContextAccessor, ConfigService configService, RoleManager<ApplicationRole> roleManager, UserManager<ApplicationUser> userManager)
        {
            this.httpClient = this.httpClient = factory.CreateClient("SQLFlowUi");
            this.baseUri = new Uri($"{navigationManager.BaseUri}odata/Identity/");
            this.navigationManager = navigationManager;
            this.configService = configService;
            this.httpContextAccessor = httpContextAccessor;
            this.roleManager = roleManager;
            this.userManager = userManager;
            //this.authenticationStateProvider = authenticationStateProvider;
            
        }

        public async Task<ApplicationAuthenticationState> GetAuthenticationStateAsync()
        {
            /*
            var authState = await authenticationStateProvider.GetAuthenticationStateAsync();
            var user = authState.User;

            // Map the AuthenticationState to your ApplicationAuthenticationState
            return new ApplicationAuthenticationState
            {
                IsAuthenticated = user.Identity?.IsAuthenticated ?? false,
                Name = user.Identity?.Name,
                Claims = user.Claims.Select(c => new ApplicationClaim { Type = c.Type, Value = c.Value })
                // ... map other claims if needed
            };
            */
            
            var uri = new Uri($"{navigationManager.BaseUri}Account/CurrentUser");

            var response = await httpClient.SendAsync(new HttpRequestMessage(HttpMethod.Post, uri));

            return await response.ReadAsync<ApplicationAuthenticationState>();
            
        }

        public async Task<IEnumerable<ApplicationRole>> GetRolesAsync()
        {
            return await roleManager.Roles.ToListAsync();

            /*  
            var uri = new Uri(baseUri, $"ApplicationRoles");

            uri = uri.GetODataUri();

            var response = await httpClient.GetAsync(uri);

            var result = await response.ReadAsync<ODataServiceResult<ApplicationRole>>();

            return result.Value;
            */

        }

        public async Task<ApplicationRole> CreateRoleAsync(ApplicationRole role)
        {
            var result = await roleManager.CreateAsync(role);

            if (!result.Succeeded)
            {
                var message = string.Join(", ", result.Errors.Select(error => error.Description));
                throw new ApplicationException(message); // Or handle the error differently
            }

            return role;
            /*
            var uri = new Uri(baseUri, $"ApplicationRoles");

            var content = new StringContent(ODataJsonSerializer.Serialize(role), Encoding.UTF8, "application/json");

            var response = await httpClient.PostAsync(uri, content);

            return await response.ReadAsync<ApplicationRole>();
            */
        }

        public async Task<HttpResponseMessage> DeleteRoleAsync(string id)
        {
            var role = await roleManager.FindByIdAsync(id);
            if (role == null)
            {
                return new HttpResponseMessage(System.Net.HttpStatusCode.NotFound);
            }

            var result = await roleManager.DeleteAsync(role);

            if (!result.Succeeded)
            {
                var message = string.Join(", ", result.Errors.Select(error => error.Description));
                return new HttpResponseMessage(System.Net.HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(message)
                };
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.NoContent);
            

            /*var uri = new Uri(baseUri, $"ApplicationRoles('{id}')");

            return await httpClient.DeleteAsync(uri);
            */
        }

        public async Task<IEnumerable<ApplicationUser>> GetUsersAsync()
        {
            return await userManager.Users.ToListAsync();
            /*
            var uri = new Uri(baseUri, $"ApplicationUsers");


            uri = uri.GetODataUri();

            var response = await httpClient.GetAsync(uri);

            var result = await response.ReadAsync<ODataServiceResult<ApplicationUser>>();

            return result.Value;
            */
        }

        public async Task<ApplicationUser> CreateUserAsync(ApplicationUser user)
        {

            user.UserName = user.Email;
            user.EmailConfirmed = true;
            
            var result = await userManager.CreateAsync(user, user.Password);

            if (!result.Succeeded)
            {
                // Construct a meaningful error message (you can customize this)
                var errorMessage = string.Join(", ", result.Errors.Select(e => e.Description));
                throw new InvalidOperationException($"User creation failed: {errorMessage}");
            }

            return user; // Return the created user if successful
            
            /*
            var uri = new Uri(baseUri, $"ApplicationUsers");
            var content = new StringContent(JsonSerializer.Serialize(user), Encoding.UTF8, "application/json");
            var response = await httpClient.PostAsync(uri, content);
            return await response.ReadAsync<ApplicationUser>();
            */
        }

        public async Task<HttpResponseMessage> DeleteUserAsync(string id)
        {
            var user = await userManager.FindByIdAsync(id);
            if (user == null)
            {
                return new HttpResponseMessage(HttpStatusCode.NotFound);
            }

            var result = await userManager.DeleteAsync(user);

            if (!result.Succeeded)
            {
                var errorMessage = string.Join(", ", result.Errors.Select(e => e.Description));
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    Content = new StringContent(errorMessage)
                };
            }

            return new HttpResponseMessage(HttpStatusCode.NoContent); // Successful deletion
            /*
            var uri = new Uri(baseUri, $"ApplicationUsers('{id}')");

            return await httpClient.DeleteAsync(uri);
            */
        }

        public async Task<ApplicationUser> GetUserByIdAsync(string id)
        {
            var user = await userManager.Users
                .Include(u => u.Roles) // Eagerly load user roles
                .FirstOrDefaultAsync(u => u.Id == id);

            if (user == null)
            {
                throw new InvalidOperationException($"User with ID {id} not found.");
            }
            return user;
            
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

        public async Task<ApplicationUser> UpdateUserAsync(string id, ApplicationUser user)
        {
            var existingUser = await userManager.FindByIdAsync(id);

            if (existingUser == null)
            {
                throw new InvalidOperationException($"User with ID {id} not found.");
            }

            // Update user properties (exclude password and roles for now)
            existingUser.UserName = user.Email;
            existingUser.Email = user.Email;
            // ... (update other properties as needed) ...

            var result = await userManager.UpdateAsync(existingUser);

            if (!result.Succeeded)
            {
                var errorMessage = string.Join(", ", result.Errors.Select(e => e.Description));
                throw new InvalidOperationException($"User update failed: {errorMessage}");
            }

            // Handle password update separately if needed
            if (!string.IsNullOrEmpty(user.Password))
            {
                var passwordChangeResult = await userManager.ChangePasswordAsync(existingUser, null, user.Password); // Assuming null for old password
                if (!passwordChangeResult.Succeeded)
                {
                    var passwordErrorMessage = string.Join(", ", passwordChangeResult.Errors.Select(e => e.Description));
                    throw new InvalidOperationException($"Password update failed: {passwordErrorMessage}");
                }
            }

            // Handle role updates separately if needed
            if (user.Roles != null && user.Roles.Any())
            {
                var currentRoles = await userManager.GetRolesAsync(existingUser);
                var rolesToAdd = user.Roles.Select(r => r.Name).Except(currentRoles);
                var rolesToRemove = currentRoles.Except(user.Roles.Select(r => r.Name));

                if (rolesToAdd.Any())
                {
                    await userManager.AddToRolesAsync(existingUser, rolesToAdd);
                }

                if (rolesToRemove.Any())
                {
                    await userManager.RemoveFromRolesAsync(existingUser, rolesToRemove);
                }
            }

            return existingUser; // Return the updated user
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

        public async Task ChangePasswordAsync(string oldPassword, string newPassword)
        {
            var user = await userManager.GetUserAsync(httpContextAccessor.HttpContext?.User);
            if (user == null)
            {
                throw new InvalidOperationException("User not found in current context.");
            }

            var result = await userManager.ChangePasswordAsync(user, oldPassword, newPassword);
            if (!result.Succeeded)
            {
                var errors = string.Join(", ", result.Errors.Select(e => e.Description));
                throw new InvalidOperationException($"Password change failed: {errors}");
            }
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

        public async Task ResetPasswordAsync(string userName)
        {
            var user = await userManager.FindByNameAsync(userName);
            if (user == null)
            {
                throw new InvalidOperationException($"User with username {userName} not found.");
            }

            // Generate a reset token
            var token = await userManager.GeneratePasswordResetTokenAsync(user);
            if (string.IsNullOrEmpty(token))
            {
                throw new InvalidOperationException("Failed to generate password reset token.");
            }

            // You'll need to implement the logic to send the token to the user
            // This could involve sending an email or SMS with instructions on how to reset the password
            // You can replace the comment below with your actual implementation
            await SendResetPasswordTokenToUserAsync(user, token);
            
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
            }*/
        }

        // Helper method to send the reset token to the user (replace with your implementation)
        private async Task SendResetPasswordTokenToUserAsync(ApplicationUser user, string token)
        {
            // ... Your email/SMS sending logic here ...
        }
    }

}
