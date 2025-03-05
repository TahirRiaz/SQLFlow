using SQLFlowUi.Models;

namespace SQLFlowUi.Services
{
    public interface IUserInformationService
    {
        Task<ApplicationUser> GetUserByIdAsync(string id);
        // Add other methods to fetch user-related data as needed
    }
}
