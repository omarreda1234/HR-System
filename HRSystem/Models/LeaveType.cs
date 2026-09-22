#nullable disable
namespace HRSystem.Models;

public class LeaveType
{
    public int Id { get; set; }
    public string Name { get; set; }               // Annual, Sick, Casual, Unpaid
    public string NameAr { get; set; }             // سنوية، مرضية، عارضة، بدون أجر
    public int DefaultDaysPerYear { get; set; }
    public bool RequiresApproval { get; set; } = true;
    public bool IsPaid { get; set; } = true;
    public string ColorClass { get; set; } = "primary"; // for UI badges

    public virtual ICollection<LeaveBalance> Balances { get; set; } = new List<LeaveBalance>();
    public virtual ICollection<LeaveRequest> Requests { get; set; } = new List<LeaveRequest>();
}
