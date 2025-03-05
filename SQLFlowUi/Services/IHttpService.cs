// For HttpResponseMessage
using SQLFlowUi.Models;

namespace SQLFlowUi.Services
{
    public interface IHttpService
    {
        Task<ApplicationAuthenticationState> GetAuthenticationStateAsync();
        Task<IEnumerable<ApplicationRole>> GetRolesAsync();
        Task<ApplicationRole> CreateRoleAsync(ApplicationRole role);
        Task<HttpResponseMessage> DeleteRoleAsync(string id);
        Task<IEnumerable<ApplicationUser>> GetUsersAsync();
        Task<ApplicationUser> CreateUserAsync(ApplicationUser user);
        Task<HttpResponseMessage> DeleteUserAsync(string id);
        Task<ApplicationUser> GetUserByIdAsync(string id);
        Task<ApplicationUser> UpdateUserAsync(string id, ApplicationUser user);
        Task ChangePasswordAsync(string oldPassword, string newPassword);
        Task ResetPasswordAsync(string userName);
    }
}