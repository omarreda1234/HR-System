#nullable disable
namespace HRSystem.Models;

public class LeaveBalance
{
    public int Id { get; set; }
    public string EmployeeNo { get; set; }   // FK → Employee.No
    public int LeaveTypeId { get; set; }
    public int Year { get; set; }
    public decimal TotalDays { get; set; }
    public decimal UsedDays { get; set; }
    public decimal PendingDays { get; set; }
    public decimal RemainingDays => TotalDays - UsedDays - PendingDays;

    public virtual Employee Employee { get; set; }
    public virtual LeaveType LeaveType { get; set; }
}
