using HRSystem.Models;
using Microsoft.EntityFrameworkCore;

namespace HRSystem.Services;

public class PayrollService
{
    private readonly HRContext _db;
    private readonly WhatsAppService _whatsApp;
    private readonly IWebHostEnvironment _env;
    private readonly ILogger<PayrollService> _logger;

    public PayrollService(HRContext db, WhatsAppService whatsApp,
        IWebHostEnvironment env, ILogger<PayrollService> logger)
    {
        _db = db; _whatsApp = whatsApp; _env = env; _logger = logger;
    }

    // ── Generate payslip for one employee ───────────────────────────────────
    public async Task<Payslip> GeneratePayslipAsync(string employeeNo, int month, int year)
    {
        // 1. Prevent duplicates
        bool exists = await _db.Payslips.AnyAsync(p =>
            p.EmployeeNo == employeeNo && p.Month == month && p.Year == year);
        if (exists) throw new InvalidOperationException(
            $"قسيمة الراتب لهذا الموظف لشهر {month}/{year} موجودة بالفعل.");

        // 2. Load active salary structure
        var salary = await _db.SalaryStructures.FirstOrDefaultAsync(s =>
            s.EmployeeNo == employeeNo && s.IsActive);
        if (salary == null) throw new InvalidOperationException(
            "لا توجد هيكلة راتب نشطة لهذا الموظف.");

        // 3. Active loans
        var loans = await _db.EmployeeLoans
            .Where(l => l.EmployeeNo == employeeNo && l.Status == LoanStatus.Active)
            .ToListAsync();
        decimal loanTotal = loans.Sum(l => Math.Min(l.MonthlyInstallment, l.RemainingAmount));

        // 4. Build payslip
        decimal gross           = salary.GrossSalary;
        decimal tax             = Math.Round(gross * salary.TaxRate, 2);
        decimal si              = Math.Round(salary.BaseSalary * salary.SocialInsuranceRate, 2);
        decimal totalDeductions = tax + si + loanTotal;

        var payslip = new Payslip
        {
            EmployeeNo            = employeeNo,
            Month                 = month,
            Year                  = year,
            BaseSalary            = salary.BaseSalary,
            HousingAllowance      = salary.HousingAllowance,
            TransportAllowance    = salary.TransportAllowance,
            MedicalAllowance      = salary.MedicalAllowance,
            OtherAllowances       = salary.OtherAllowances,
            GrossSalary           = gross,
            TaxAmount             = tax,
            SocialInsuranceAmount = si,
            LoanDeductionAmount   = loanTotal,
            TotalDeductions       = totalDeductions,
            NetSalary             = gross - totalDeductions,
            GeneratedAt           = DateTime.Now,
            IsPublished           = false
        };

        _db.Payslips.Add(payslip);
        await _db.SaveChangesAsync();

        // 5. Process loan deductions
        foreach (var loan in loans)
        {
            decimal installment = Math.Min(loan.MonthlyInstallment, loan.RemainingAmount);
            loan.RemainingAmount = Math.Round(loan.RemainingAmount - installment, 2);
            if (loan.RemainingAmount <= 0) loan.Status = LoanStatus.Completed;

            _db.LoanDeductions.Add(new LoanDeduction
            {
                LoanId        = loan.Id,
                PayslipId     = payslip.Id,
                Amount        = installment,
                DeductionDate = DateTime.Now
            });
        }

        // 6. Generate PDF
        payslip.PdfPath = await GeneratePdfAsync(payslip);
        payslip.IsPublished = true;
        await _db.SaveChangesAsync();

        // 7. Notify employee
        var emp = await _db.Employees.FindAsync(employeeNo);
        if (emp != null)
        {
            await _whatsApp.SendAsync(emp.MobilePhoneNo,
                $"مرحباً {emp.SearchName}،\n" +
                $"تم إصدار قسيمة راتبك لشهر {month}/{year}.\n" +
                $"صافي الراتب: {payslip.NetSalary:N2} جنيه.\n" +
                $"يمكنك تحميل القسيمة من بوابة الموظفين.");
        }

        return payslip;
    }

    // ── Generate payslips for ALL active employees (Hangfire monthly job) ───
    public async Task GenerateMonthlyPayrollAsync(int month, int year)
    {
        var employeeNos = await _db.Employees
            .Where(e => e.Disabled != true && e.EmployeeStatus == "Active")
            .Select(e => e.No)
            .ToListAsync();

        foreach (var no in employeeNos)
        {
            try   { await GeneratePayslipAsync(no, month, year); }
            catch (Exception ex)
            { _logger.LogWarning("Payroll skip {No}: {Msg}", no, ex.Message); }
        }
    }

    // ── Simple HTML→PDF via a minimal in-memory HTML string ─────────────────
    // NOTE: Replace with QuestPDF or Rotativa for a production-grade PDF
    private async Task<string> GeneratePdfAsync(Payslip p)
    {
        string dir = Path.Combine(_env.WebRootPath, "payslips");
        Directory.CreateDirectory(dir);
        string file = $"payslip_{p.EmployeeNo}_{p.Year}_{p.Month:D2}.html";
        string path = Path.Combine(dir, file);

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("<!DOCTYPE html>");
        sb.AppendLine("<html dir=\"rtl\" lang=\"ar\"><head><meta charset=\"UTF-8\">");
        sb.AppendLine("<style>");
        sb.AppendLine("body { font-family: Arial, sans-serif; padding: 30px; color: #333; }");
        sb.AppendLine("h2 { color: #4e73df; border-bottom: 2px solid #4e73df; padding-bottom: 8px; }");
        sb.AppendLine("table { width: 100%; border-collapse: collapse; margin-top: 20px; }");
        sb.AppendLine("td { padding: 10px; border: 1px solid #ddd; }");
        sb.AppendLine(".label { background: #f8f9fc; font-weight: bold; width: 50%; }");
        sb.AppendLine(".net { font-size: 1.4em; color: #1cc88a; font-weight: bold; }");
        sb.AppendLine(".total { background: #f8f9fc; font-weight: bold; }");
        sb.AppendLine("</style></head><body>");
        sb.AppendLine($"<h2>قسيمة الراتب &mdash; {p.Month:D2}/{p.Year}</h2>");
        sb.AppendLine($"<p><strong>كود الموظف:</strong> {p.EmployeeNo}</p>");
        sb.AppendLine("<table>");
        sb.AppendLine($"<tr><td class='label'>الراتب الأساسي</td><td>{p.BaseSalary:N2}</td></tr>");
        sb.AppendLine($"<tr><td class='label'>بدل سكن</td><td>{p.HousingAllowance:N2}</td></tr>");
        sb.AppendLine($"<tr><td class='label'>بدل نقل</td><td>{p.TransportAllowance:N2}</td></tr>");
        sb.AppendLine($"<tr><td class='label'>بدل طبي</td><td>{p.MedicalAllowance:N2}</td></tr>");
        sb.AppendLine($"<tr><td class='label'>بدلات أخرى</td><td>{p.OtherAllowances:N2}</td></tr>");
        sb.AppendLine($"<tr class='total'><td>إجمالي الراتب</td><td>{p.GrossSalary:N2}</td></tr>");
        sb.AppendLine($"<tr><td class='label'>الضريبة</td><td>({p.TaxAmount:N2})</td></tr>");
        sb.AppendLine($"<tr><td class='label'>التأمينات الاجتماعية</td><td>({p.SocialInsuranceAmount:N2})</td></tr>");
        sb.AppendLine($"<tr><td class='label'>اقتطاع السلفة/القرض</td><td>({p.LoanDeductionAmount:N2})</td></tr>");
        sb.AppendLine($"<tr class='total'><td>إجمالي الخصومات</td><td>({p.TotalDeductions:N2})</td></tr>");
        sb.AppendLine($"<tr><td class='label'>صافي الراتب</td><td class='net'>{p.NetSalary:N2}</td></tr>");
        sb.AppendLine("</table>");
        sb.AppendLine($"<p style='color:#999;font-size:0.8em;margin-top:30px;'>تم الإصدار في: {p.GeneratedAt:yyyy/MM/dd HH:mm}</p>");
        sb.AppendLine("</body></html>");

        await File.WriteAllTextAsync(path, sb.ToString(), System.Text.Encoding.UTF8);
        return $"/payslips/{file}";
    }
}
