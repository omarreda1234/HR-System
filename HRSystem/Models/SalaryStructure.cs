#nullable disable
namespace HRSystem.Models;

public class SalaryStructure
{
    public int Id { get; set; }
    public string EmployeeNo { get; set; }           // FK → Employee.No
    public decimal BaseSalary { get; set; }
    public decimal HousingAllowance { get; set; }
    public decimal TransportAllowance { get; set; }
    public decimal MedicalAllowance { get; set; }
    public decimal OtherAllowances { get; set; }
    public decimal TaxRate { get; set; }             // 0.10 = 10%
    public decimal SocialInsuranceRate { get; set; } // 0.11 = 11%
    public DateTime EffectiveFrom { get; set; } = DateTime.Today;
    public bool IsActive { get; set; } = true;
    public string Notes { get; set; }

    // Computed helpers
    public decimal GrossSalary => BaseSalary + HousingAllowance +
                                  TransportAllowance + MedicalAllowance + OtherAllowances;

    public virtual Employee Employee { get; set; }
}
