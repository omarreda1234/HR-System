#nullable disable
namespace HRSystem.Models;

public class Payslip
{
    public int Id { get; set; }
    public string EmployeeNo { get; set; }       // FK → Employee.No
    public int Month { get; set; }
    public int Year { get; set; }

    // Earnings
    public decimal BaseSalary { get; set; }
    public decimal HousingAllowance { get; set; }
    public decimal TransportAllowance { get; set; }
    public decimal MedicalAllowance { get; set; }
    public decimal OtherAllowances { get; set; }
    public decimal GrossSalary { get; set; }

    // Deductions
    public decimal TaxAmount { get; set; }
    public decimal SocialInsuranceAmount { get; set; }
    public decimal LoanDeductionAmount { get; set; }
    public decimal OtherDeductions { get; set; }
    public decimal TotalDeductions { get; set; }

    // Net
    public decimal NetSalary { get; set; }

    public string PdfPath { get; set; }
    public DateTime GeneratedAt { get; set; } = DateTime.Now;
    public bool IsPublished { get; set; } = false;

    public virtual Employee Employee { get; set; }
    public virtual ICollection<LoanDeduction> LoanDeductions { get; set; } = new List<LoanDeduction>();
}
