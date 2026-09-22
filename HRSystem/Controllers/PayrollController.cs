using HRSystem.Models;
using HRSystem.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace HRSystem.Controllers;

[Authorize(Roles = "SuperAdmin,HR")]
public class PayrollController : Controller
{
    private readonly HRContext _db;
    private readonly PayrollService _payrollService;
    private readonly IWebHostEnvironment _env;

    public PayrollController(HRContext db, PayrollService payrollService,
        IWebHostEnvironment env)
    {
        _db = db; _payrollService = payrollService; _env = env;
    }

    // ── Dashboard: list payslips for a month ─────────────────────────────
    public async Task<IActionResult> Index(int month = 0, int year = 0)
    {
        if (month == 0) month = DateTime.Now.Month;
        if (year  == 0) year  = DateTime.Now.Year;

        var payslips = await _db.Payslips
            .Include(p => p.Employee)
            .Where(p => p.Month == month && p.Year == year)
            .OrderBy(p => p.Employee.SearchName)
            .ToListAsync();

        ViewBag.Month = month;
        ViewBag.Year  = year;
        ViewBag.TotalNet   = payslips.Sum(p => p.NetSalary);
        ViewBag.TotalGross = payslips.Sum(p => p.GrossSalary);
        ViewBag.Count      = payslips.Count;
        return View(payslips);
    }

    // ── Salary Structures ─────────────────────────────────────────────────
    public async Task<IActionResult> SalaryStructures()
    {
        var list = await _db.SalaryStructures
            .Include(s => s.Employee)
            .OrderBy(s => s.Employee.SearchName)
            .ToListAsync();
        return View(list);
    }

    [HttpGet]
    public async Task<IActionResult> EditSalary(string empNo)
    {
        var salary = await _db.SalaryStructures
            .FirstOrDefaultAsync(s => s.EmployeeNo == empNo && s.IsActive)
            ?? new SalaryStructure { EmployeeNo = empNo, IsActive = true };

        ViewBag.Employee = await _db.Employees.FindAsync(empNo);
        return View(salary);
    }

    [HttpPost]
    public async Task<IActionResult> EditSalary(SalaryStructure model)
    {
        if (!ModelState.IsValid)
        {
            ViewBag.Employee = await _db.Employees.FindAsync(model.EmployeeNo);
            return View(model);
        }

        var existing = await _db.SalaryStructures
            .FirstOrDefaultAsync(s => s.EmployeeNo == model.EmployeeNo && s.IsActive);

        if (existing == null)
        {
            model.EffectiveFrom = DateTime.Today;
            _db.SalaryStructures.Add(model);
        }
        else
        {
            existing.BaseSalary            = model.BaseSalary;
            existing.HousingAllowance      = model.HousingAllowance;
            existing.TransportAllowance    = model.TransportAllowance;
            existing.MedicalAllowance      = model.MedicalAllowance;
            existing.OtherAllowances       = model.OtherAllowances;
            existing.TaxRate               = model.TaxRate;
            existing.SocialInsuranceRate   = model.SocialInsuranceRate;
            existing.Notes                 = model.Notes;
        }

        await _db.SaveChangesAsync();
        TempData["Success"] = "تم حفظ هيكلة الراتب.";
        return RedirectToAction(nameof(SalaryStructures));
    }

    // ── Generate Payslips ─────────────────────────────────────────────────
    [HttpPost]
    public async Task<IActionResult> GenerateOne(string empNo, int month, int year)
    {
        try
        {
            await _payrollService.GeneratePayslipAsync(empNo, month, year);
            TempData["Success"] = "تم إصدار القسيمة بنجاح.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(Index), new { month, year });
    }

    [HttpPost]
    public async Task<IActionResult> GenerateAll(int month, int year)
    {
        await _payrollService.GenerateMonthlyPayrollAsync(month, year);
        TempData["Success"] = $"تم إصدار مسير رواتب {month}/{year} لجميع الموظفين.";
        return RedirectToAction(nameof(Index), new { month, year });
    }

    // ── Publish / Unpublish ───────────────────────────────────────────────
    [HttpPost]
    public async Task<IActionResult> Publish(int id, bool publish)
    {
        var payslip = await _db.Payslips.FindAsync(id);
        if (payslip != null)
        {
            payslip.IsPublished = publish;
            await _db.SaveChangesAsync();
        }
        return RedirectToAction(nameof(Index),
            new { month = payslip?.Month, year = payslip?.Year });
    }

    // ── Download Payslip ──────────────────────────────────────────────────
    [Authorize] // Allow all logged-in (ESS also downloads via ESS controller)
    public async Task<IActionResult> Download(int id)
    {
        var payslip = await _db.Payslips.FindAsync(id);
        if (payslip == null || string.IsNullOrEmpty(payslip.PdfPath))
            return NotFound();

        string absPath = Path.Combine(_env.WebRootPath,
            payslip.PdfPath.TrimStart('/').Replace('/', Path.DirectorySeparatorChar));

        if (!System.IO.File.Exists(absPath)) return NotFound("ملف القسيمة غير موجود.");

        string mime = payslip.PdfPath.EndsWith(".pdf")
            ? "application/pdf" : "text/html";
        return PhysicalFile(absPath, mime,
            $"Payslip_{payslip.EmployeeNo}_{payslip.Year}_{payslip.Month:D2}.html");
    }

    // ── Loans ─────────────────────────────────────────────────────────────
    public async Task<IActionResult> Loans()
    {
        var loans = await _db.EmployeeLoans
            .Include(l => l.Employee)
            .OrderBy(l => l.Status)
            .ThenBy(l => l.Employee.SearchName)
            .ToListAsync();
        ViewBag.Employees = await _db.Employees.Where(e => e.Disabled != true)
            .Select(e => new SelectListItem(e.SearchName + " (" + e.No + ")", e.No))
            .ToListAsync();
        return View(loans);
    }

    [HttpPost]
    public async Task<IActionResult> AddLoan(EmployeeLoan model)
    {
        model.RemainingAmount = model.TotalAmount;
        model.StartDate       = DateTime.Today;
        model.Status          = LoanStatus.Active;
        model.CreatedAt       = DateTime.Now;
        _db.EmployeeLoans.Add(model);
        await _db.SaveChangesAsync();
        TempData["Success"] = "تم إضافة السلفة/القرض.";
        return RedirectToAction(nameof(Loans));
    }

    [HttpPost]
    public async Task<IActionResult> CancelLoan(int id)
    {
        var loan = await _db.EmployeeLoans.FindAsync(id);
        if (loan != null) { loan.Status = LoanStatus.Cancelled; await _db.SaveChangesAsync(); }
        TempData["Success"] = "تم إلغاء السلفة.";
        return RedirectToAction(nameof(Loans));
    }
}
