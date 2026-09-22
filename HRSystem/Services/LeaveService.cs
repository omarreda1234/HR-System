using HRSystem.Models;
using Microsoft.EntityFrameworkCore;

namespace HRSystem.Services;

public class LeaveService
{
    private readonly HRContext _db;
    private readonly WhatsAppService _whatsApp;

    public LeaveService(HRContext db, WhatsAppService whatsApp)
    {
        _db = db;
        _whatsApp = whatsApp;
    }

    // ── Step 1: Employee submits a request ──────────────────────────────────
    public async Task<LeaveRequest> SubmitRequestAsync(
        string employeeNo, int leaveTypeId,
        DateTime start, DateTime end, string reason)
    {
        int days = (int)(end.Date - start.Date).TotalDays + 1;
        if (days <= 0) throw new InvalidOperationException("تاريخ الانتهاء يجب أن يكون بعد تاريخ البدء.");

        var balance = await _db.LeaveBalances.FirstOrDefaultAsync(b =>
            b.EmployeeNo == employeeNo &&
            b.LeaveTypeId == leaveTypeId &&
            b.Year == DateTime.Today.Year);

        if (balance == null || balance.RemainingDays < days)
            throw new InvalidOperationException("رصيد الإجازة غير كافٍ.");

        bool overlap = await _db.LeaveRequests.AnyAsync(r =>
            r.EmployeeNo == employeeNo &&
            r.Status != LeaveStatus.Rejected &&
            r.Status != LeaveStatus.Cancelled &&
            r.StartDate <= end && r.EndDate >= start);
        if (overlap)
            throw new InvalidOperationException("يوجد طلب إجازة متداخل مع هذه الفترة.");

        var request = new LeaveRequest
        {
            EmployeeNo  = employeeNo,
            LeaveTypeId = leaveTypeId,
            StartDate   = start.Date,
            EndDate     = end.Date,
            TotalDays   = days,
            Reason      = reason,
            Status      = LeaveStatus.Pending,
            CreatedAt   = DateTime.Now
        };

        balance.PendingDays += days;
        _db.LeaveRequests.Add(request);
        await _db.SaveChangesAsync();

        // Notify manager via WhatsApp
        var emp = await _db.Employees.FindAsync(employeeNo);
        if (emp?.MobilePhoneNo != null)
        {
            // Get manager (if Employee has a ManagerNo field — we'll use BranchId for now)
        }
        return request;
    }

    // ── Step 2: Manager approves or rejects ─────────────────────────────────
    public async Task ManagerActionAsync(int requestId, string managerNo,
        bool approve, string notes)
    {
        var req = await _db.LeaveRequests
            .Include(r => r.Employee)
            .Include(r => r.LeaveType)
            .FirstOrDefaultAsync(r => r.Id == requestId);

        if (req == null || req.Status != LeaveStatus.Pending)
            throw new InvalidOperationException("الطلب غير موجود أو تم البت فيه مسبقاً.");

        req.ManagerNo         = managerNo;
        req.ManagerActionDate = DateTime.Now;
        req.ManagerNotes      = notes;
        req.Status = approve ? LeaveStatus.ManagerApproved : LeaveStatus.Rejected;

        if (!approve)
        {
            var balance = await GetBalanceAsync(req.EmployeeNo, req.LeaveTypeId);
            if (balance != null) balance.PendingDays -= req.TotalDays;

            await _whatsApp.SendAsync(req.Employee?.MobilePhoneNo,
                $"عذراً، تم رفض طلب إجازتك ({req.LeaveType?.NameAr}) " +
                $"من {req.StartDate:yyyy/MM/dd} إلى {req.EndDate:yyyy/MM/dd}. " +
                $"السبب: {notes}");
        }
        else
        {
            await _whatsApp.SendAsync(req.Employee?.MobilePhoneNo,
                $"تمت موافقة مديرك على طلب إجازتك ({req.LeaveType?.NameAr}) " +
                $"وهو الآن في انتظار الموافقة النهائية من HR.");
        }

        await _db.SaveChangesAsync();
    }

    // ── Step 3: HR finalizes ─────────────────────────────────────────────────
    public async Task HRActionAsync(int requestId, string hrUserId,
        bool approve, string notes)
    {
        var req = await _db.LeaveRequests
            .Include(r => r.Employee)
            .Include(r => r.LeaveType)
            .FirstOrDefaultAsync(r => r.Id == requestId);

        if (req == null || req.Status != LeaveStatus.ManagerApproved)
            throw new InvalidOperationException("الطلب لم تتم موافقة المدير عليه بعد.");

        req.HRUserId      = hrUserId;
        req.HRActionDate  = DateTime.Now;
        req.HRNotes       = notes;
        req.Status = approve ? LeaveStatus.HRApproved : LeaveStatus.Rejected;

        var balance = await GetBalanceAsync(req.EmployeeNo, req.LeaveTypeId);
        if (balance != null)
        {
            balance.PendingDays -= req.TotalDays;
            if (approve) balance.UsedDays += req.TotalDays;
        }

        string msg = approve
            ? $"تهانينا! تمت الموافقة النهائية على إجازتك ({req.LeaveType?.NameAr}) " +
              $"من {req.StartDate:yyyy/MM/dd} إلى {req.EndDate:yyyy/MM/dd}."
            : $"عذراً، تم رفض طلب إجازتك ({req.LeaveType?.NameAr}) من HR. السبب: {notes}";

        await _whatsApp.SendAsync(req.Employee?.MobilePhoneNo, msg);
        await _db.SaveChangesAsync();
    }

    // ── Cancel (by employee) ─────────────────────────────────────────────────
    public async Task CancelRequestAsync(int requestId, string employeeNo)
    {
        var req = await _db.LeaveRequests.FindAsync(requestId);
        if (req == null || req.EmployeeNo != employeeNo)
            throw new InvalidOperationException("الطلب غير موجود.");
        if (req.Status == LeaveStatus.HRApproved)
            throw new InvalidOperationException("لا يمكن إلغاء إجازة معتمدة نهائياً.");

        var balance = await GetBalanceAsync(req.EmployeeNo, req.LeaveTypeId);
        if (balance != null)
        {
            if (req.Status == LeaveStatus.Pending || req.Status == LeaveStatus.ManagerApproved)
                balance.PendingDays -= req.TotalDays;
        }
        req.Status = LeaveStatus.Cancelled;
        await _db.SaveChangesAsync();
    }

    // ── Refresh annual leave balances (called by Hangfire on Jan 1st) ────────
    public async Task RefreshAnnualBalancesAsync(int year)
    {
        var employees  = await _db.Employees.Where(e => e.Disabled != true).ToListAsync();
        var leaveTypes = await _db.LeaveTypes.ToListAsync();

        foreach (var emp in employees)
        {
            foreach (var lt in leaveTypes)
            {
                bool exists = await _db.LeaveBalances.AnyAsync(b =>
                    b.EmployeeNo == emp.No && b.LeaveTypeId == lt.Id && b.Year == year);
                if (!exists)
                {
                    _db.LeaveBalances.Add(new LeaveBalance
                    {
                        EmployeeNo  = emp.No,
                        LeaveTypeId = lt.Id,
                        Year        = year,
                        TotalDays   = lt.DefaultDaysPerYear
                    });
                }
            }
        }
        await _db.SaveChangesAsync();
    }

    private async Task<LeaveBalance?> GetBalanceAsync(string empNo, int typeId) =>
        await _db.LeaveBalances.FirstOrDefaultAsync(b =>
            b.EmployeeNo == empNo &&
            b.LeaveTypeId == typeId &&
            b.Year == DateTime.Today.Year);
}
