using System.Text.Json.Serialization;
using Microsoft.AspNetCore.Identity;

namespace SQLFlowUi.Models
{
    public partial class ApplicationRole : IdentityRole
    {
        [JsonIgnore]
        public ICollection<ApplicationUser> Users { get; set; }

    }
}