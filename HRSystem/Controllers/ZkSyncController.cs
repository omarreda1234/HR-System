using System;
using System.Linq;
using System.Threading.Tasks;
using HRSystem.Models;
using HRSystem.Services;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

namespace HRSystem.Controllers
{
    [Authorize]
    public class ZkSyncController : Controller
    {
        private readonly HRContext _context;
        private readonly ZkAccessDbService _zkAccessService;

        public ZkSyncController(HRContext context, ZkAccessDbService zkAccessService)
        {
            _context = context;
            _zkAccessService = zkAccessService;
        }

        public async Task<IActionResult> Index()
        {
            var branches = await _context.Branches
                .Include(b => b.FingerDevices)
                .OrderBy(b => b.BranchName)
                .ToListAsync();

            return View(branches);
        }

        [HttpPost]
        public async Task<IActionResult> Diagnose(string path)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Json(new { success = false, summary = "يرجى كتابة أو اختيار مسار أولاً." });
            }

            var diag = await _zkAccessService.DiagnoseConnectionAsync(path);
            return Json(new
            {
                success = diag.Success,
                summary = diag.Summary,
                targetPath = diag.TargetPath,
                extractedIp = diag.ExtractedIp,
                steps = diag.Steps
            });
        }

        [HttpPost]
        public async Task<IActionResult> SaveBranchPath(int branchId, string path)
        {
            var branch = await _context.Branches.FindAsync(branchId);
            if (branch == null)
            {
                return Json(new { success = false, message = "الفرع غير موجود." });
            }

            branch.ZkAccessDbPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
            _context.Update(branch);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = $"تم حفظ مسار ZKTeco للفرع ({branch.BranchName}) بنجاح!" });
        }

        [HttpPost]
        public async Task<IActionResult> AddUserDirect(string path, string userCode, string userName, string? deviceIp)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Json(new { success = false, message = "يرجى تحديد مسار قاعدة بيانات ZKTeco." });
            }
            if (string.IsNullOrWhiteSpace(userCode) || string.IsNullOrWhiteSpace(userName))
            {
                return Json(new { success = false, message = "يرجى إدخال كود واسم الموظف." });
            }

            var (success, msg) = await _zkAccessService.AddOrUpdateUserInAccessDbAsync(path, userCode.Trim(), userName.Trim(), deviceIp);
            return Json(new { success, message = msg });
        }

        [HttpGet]
        public async Task<IActionResult> GetZkUsers(string path, string? search)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Json(new { success = false, message = "المسار غير محدد." });
            }

            var users = await _zkAccessService.GetUsersAsync(path, search, limit: 100);
            return Json(new { success = true, count = users.Count, users });
        }
    }
}
