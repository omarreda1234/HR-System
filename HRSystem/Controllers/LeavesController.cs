using HRSystem.Models;
using HRSystem.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Rendering;
using Microsoft.EntityFrameworkCore;

namespace HRSystem.Controllers;

[Authorize]
public class LeavesController : Controller
{
    private readonly HRContext _db;
    private readonly LeaveService _leaveService;
    private readonly UserManager<ApplicationUser> _userManager;

    public LeavesController(HRContext db, LeaveService leaveService,
        UserManager<ApplicationUser> userManager)
    {
        _db = db; _leaveService = leaveService; _userManager = userManager;
    }

    // ── ESS: My Requests ────────────────────────────────────────────────────
    public async Task<IActionResult> MyRequests()
    {
        string empNo = await GetCurrentEmployeeNoAsync();
        if (empNo == null) return Challenge();

        var requests = await _db.LeaveRequests
            .Include(r => r.LeaveType)
            .Where(r => r.EmployeeNo == empNo)
            .OrderByDescending(r => r.CreatedAt)
            .ToListAsync();

        ViewBag.LeaveTypes = await _db.LeaveTypes.ToListAsync();
        ViewBag.Balances = await _db.LeaveBalances
            .Include(b => b.LeaveType)
            .Where(b => b.EmployeeNo == empNo && b.Year == DateTime.Today.Year)
            .ToListAsync();

        return View(requests);
    }

    [HttpPost]
    public async Task<IActionResult> Submit(int leaveTypeId, DateTime startDate,
        DateTime endDate, string reason)
    {
        string empNo = await GetCurrentEmployeeNoAsync();
        if (empNo == null) return Challenge();

        try
        {
            await _leaveService.SubmitRequestAsync(empNo, leaveTypeId, startDate, endDate, reason);
            TempData["Success"] = "تم إرسال طلب الإجازة بنجاح وهو في انتظار موافقة المدير.";
        }
        catch (Exception ex)
        {
            TempData["Error"] = ex.Message;
        }
        return RedirectToAction(nameof(MyRequests));
    }

    [HttpPost]
    public async Task<IActionResult> Cancel(int id)
    {
        string empNo = await GetCurrentEmployeeNoAsync();
        if (empNo == null) return Challenge();
        try
        {
            await _leaveService.CancelRequestAsync(id, empNo);
            TempData["Success"] = "تم إلغاء الطلب.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(MyRequests));
    }

    // ── Manager: Inbox ──────────────────────────────────────────────────────
    [Authorize(Roles = "SuperAdmin,HR,Manager")]
    public async Task<IActionResult> ManagerInbox()
    {
        // Shows all PENDING requests (Manager sees all in this implementation;
        // refine by BranchId or direct reports as needed)
        var pending = await _db.LeaveRequests
            .Include(r => r.Employee)
            .Include(r => r.LeaveType)
            .Where(r => r.Status == LeaveStatus.Pending)
            .OrderBy(r => r.CreatedAt)
            .ToListAsync();
        return View(pending);
    }

    [HttpPost, Authorize(Roles = "SuperAdmin,HR,Manager")]
    public async Task<IActionResult> ManagerAction(int requestId, bool approve, string notes)
    {
        string managerNo = await GetCurrentEmployeeNoAsync() ?? "";
        try
        {
            await _leaveService.ManagerActionAsync(requestId, managerNo, approve, notes);
            TempData["Success"] = approve ? "تمت الموافقة بنجاح." : "تم الرفض.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(ManagerInbox));
    }

    // ── HR: Final Approval ──────────────────────────────────────────────────
    [Authorize(Roles = "SuperAdmin,HR")]
    public async Task<IActionResult> HRInbox()
    {
        var requests = await _db.LeaveRequests
            .Include(r => r.Employee)
            .Include(r => r.LeaveType)
            .Where(r => r.Status == LeaveStatus.ManagerApproved)
            .OrderBy(r => r.ManagerActionDate)
            .ToListAsync();
        return View(requests);
    }

    [HttpPost, Authorize(Roles = "SuperAdmin,HR")]
    public async Task<IActionResult> HRAction(int requestId, bool approve, string notes)
    {
        var user = await _userManager.GetUserAsync(User);
        try
        {
            await _leaveService.HRActionAsync(requestId, user.Id, approve, notes);
            TempData["Success"] = approve ? "تمت الموافقة النهائية." : "تم الرفض.";
        }
        catch (Exception ex) { TempData["Error"] = ex.Message; }
        return RedirectToAction(nameof(HRInbox));
    }

    // ── Admin: All Requests ─────────────────────────────────────────────────
    [Authorize(Roles = "SuperAdmin,HR")]
    public async Task<IActionResult> AllRequests(int? year, LeaveStatus? status)
    {
        int y = year ?? DateTime.Today.Year;
        var q = _db.LeaveRequests
            .Include(r => r.Employee)
            .Include(r => r.LeaveType)
            .Where(r => r.StartDate.Year == y)
            .AsQueryable();

        if (status.HasValue) q = q.Where(r => r.Status == status.Value);

        ViewBag.Year       = y;
        ViewBag.StatusList = Enum.GetValues<LeaveStatus>()
            .Select(s => new SelectListItem(s.ToString(), ((int)s).ToString()));
        return View(await q.OrderByDescending(r => r.CreatedAt).ToListAsync());
    }

    // ── Leave Types Management ──────────────────────────────────────────────
    [Authorize(Roles = "SuperAdmin,HR")]
    public async Task<IActionResult> LeaveTypes() =>
        View(await _db.LeaveTypes.ToListAsync());

    [HttpPost, Authorize(Roles = "SuperAdmin,HR")]
    public async Task<IActionResult> SaveLeaveType(LeaveType lt)
    {
        if (lt.Id == 0) _db.LeaveTypes.Add(lt);
        else _db.LeaveTypes.Update(lt);
        await _db.SaveChangesAsync();
        TempData["Success"] = "تم الحفظ.";
        return RedirectToAction(nameof(LeaveTypes));
    }

    // ── Balance Management ──────────────────────────────────────────────────
    [Authorize(Roles = "SuperAdmin,HR")]
    public async Task<IActionResult> Balances(string empNo)
    {
        var balances = await _db.LeaveBalances
            .Include(b => b.Employee)
            .Include(b => b.LeaveType)
            .Where(b => (empNo == null || b.EmployeeNo == empNo) &&
                        b.Year == DateTime.Today.Year)
            .ToListAsync();

        ViewBag.Employees  = await _db.Employees.Where(e => e.Disabled != true)
            .Select(e => new SelectListItem(e.SearchName + " - " + e.No, e.No)).ToListAsync();
        ViewBag.LeaveTypes = await _db.LeaveTypes.ToListAsync();
        return View(balances);
    }

    [HttpPost, Authorize(Roles = "SuperAdmin,HR")]
    public async Task<IActionResult> RefreshBalances()
    {
        await _leaveService.RefreshAnnualBalancesAsync(DateTime.Today.Year);
        TempData["Success"] = "تم تجديد أرصدة الإجازات لجميع الموظفين.";
        return RedirectToAction(nameof(Balances));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────
    private async Task<string?> GetCurrentEmployeeNoAsync()
    {
        var user = await _userManager.GetUserAsync(User);
        // Link via ApplicationUser.UserName == Employee.No convention
        // Or you can store EmployeeNo in a custom claim
        var emp = await _db.Employees
            .FirstOrDefaultAsync(e => e.No == user.UserName);
        return emp?.No;
    }
}
