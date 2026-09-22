using Microsoft.AspNetCore.Identity;

namespace HRSystem.Models;

public class ApplicationUser : IdentityUser
{
    public int? BranchId { get; set; }
    public virtual Branch Branch { get; set; }
}
