#nullable disable
namespace HRSystem.Models;

public class EmployeeDocument
{
    public int Id { get; set; }
    public string EmployeeNo { get; set; }      // FK → Employee.No
    public string DocumentType { get; set; }    // Contract, NationalId, Certificate, Other
    public string FileName { get; set; }        // original name
    public string FilePath { get; set; }        // /uploads/documents/...
    public DateTime UploadDate { get; set; } = DateTime.Now;
    public DateTime? ExpiryDate { get; set; }
    public string Notes { get; set; }

    public bool IsExpired =>
        ExpiryDate.HasValue && ExpiryDate.Value.Date < DateTime.Today;

    public int? DaysUntilExpiry =>
        ExpiryDate.HasValue ? (int)(ExpiryDate.Value.Date - DateTime.Today).TotalDays : null;

    public virtual Employee Employee { get; set; }
}
