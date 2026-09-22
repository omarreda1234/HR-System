#nullable disable
namespace HRSystem.Models;

public class EmployeeLoan
{
    public int Id { get; set; }
    public string EmployeeNo { get; set; }       // FK → Employee.No
    public decimal TotalAmount { get; set; }
    public decimal MonthlyInstallment { get; set; }
    public decimal RemainingAmount { get; set; }
    public DateTime StartDate { get; set; } = DateTime.Today;
    public LoanStatus Status { get; set; } = LoanStatus.Active;
    public string Notes { get; set; }
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public int PaidInstallments => TotalAmount == 0 ? 0 :
        (int)Math.Round((TotalAmount - RemainingAmount) / MonthlyInstallment);

    public virtual Employee Employee { get; set; }
    public virtual ICollection<LoanDeduction> Deductions { get; set; } = new List<LoanDeduction>();
}

public class LoanDeduction
{
    public int Id { get; set; }
    public int LoanId { get; set; }
    public int PayslipId { get; set; }
    public decimal Amount { get; set; }
    public DateTime DeductionDate { get; set; } = DateTime.Now;

    public virtual EmployeeLoan Loan { get; set; }
    public virtual Payslip Payslip { get; set; }
}

public enum LoanStatus { Active, Completed, Cancelled }
