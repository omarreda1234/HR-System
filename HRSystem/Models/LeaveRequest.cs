#nullable disable
namespace HRSystem.Models;

public class LeaveRequest
{
    public int Id { get; set; }
    public string EmployeeNo { get; set; }   // FK → Employee.No
    public int LeaveTypeId { get; set; }
    public DateTime StartDate { get; set; }
    public DateTime EndDate { get; set; }
    public int TotalDays { get; set; }
    public string Reason { get; set; }
    public LeaveStatus Status { get; set; } = LeaveStatus.Pending;

    // Step 2: Manager
    public string ManagerNo { get; set; }        // FK → Employee.No (approving manager)
    public DateTime? ManagerActionDate { get; set; }
    public string ManagerNotes { get; set; }

    // Step 3: HR
    public string HRUserId { get; set; }         // ApplicationUser.Id
    public DateTime? HRActionDate { get; set; }
    public string HRNotes { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public virtual Employee Employee { get; set; }
    public virtual LeaveType LeaveType { get; set; }
}

public enum LeaveStatus
{
    Pending         = 0,
    ManagerApproved = 1,
    HRApproved      = 2,
    Rejected        = 3,
    Cancelled       = 4
}
