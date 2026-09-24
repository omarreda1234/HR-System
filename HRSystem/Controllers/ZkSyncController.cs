using System;
using System.Linq;
using System.Text.RegularExpressions;
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

        private async Task<(string? username, string? password)> ResolveCredentialsAsync(string path, string? username, string? password)
        {
            if (!string.IsNullOrWhiteSpace(username)) return (username, password);

            if (string.IsNullOrWhiteSpace(path)) return (null, null);

            var match = Regex.Match(path, @"^\\\\([^\\]+)");
            string ipOrHost = match.Success ? match.Groups[1].Value : "";

            var branch = await _context.Branches.FirstOrDefaultAsync(b => 
                (b.ZkAccessDbPath != null && b.ZkAccessDbPath.Trim() == path.Trim()) ||
                (!string.IsNullOrEmpty(ipOrHost) && b.ZkAccessDbPath != null && b.ZkAccessDbPath.Contains(ipOrHost)) ||
                (!string.IsNullOrEmpty(ipOrHost) && b.VpnIp != null && b.VpnIp.Trim() == ipOrHost));

            if (branch != null && !string.IsNullOrWhiteSpace(branch.ZkUsername))
            {
                return (branch.ZkUsername, branch.ZkPassword);
            }

            return (null, null);
        }

        [HttpPost]
        public async Task<IActionResult> Diagnose(string path, string? username, string? password)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Json(new { success = false, summary = "يرجى كتابة أو اختيار مسار أولاً." });
            }

            (username, password) = await ResolveCredentialsAsync(path, username, password);

            var diag = await _zkAccessService.DiagnoseConnectionAsync(path, username, password);
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
        public async Task<IActionResult> SaveBranchPath(int branchId, string path, string? username, string? password)
        {
            var branch = await _context.Branches.FindAsync(branchId);
            if (branch == null)
            {
                return Json(new { success = false, message = "الفرع غير موجود." });
            }

            branch.ZkAccessDbPath = string.IsNullOrWhiteSpace(path) ? null : path.Trim();
            branch.ZkUsername = string.IsNullOrWhiteSpace(username) ? null : username.Trim();
            if (!string.IsNullOrWhiteSpace(password)) branch.ZkPassword = password;

            _context.Update(branch);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = $"تم حفظ إعدادات وبيانات دخول ZKTeco للفرع ({branch.BranchName}) بنجاح!" });
        }

        [HttpPost]
        public async Task<IActionResult> AddUserDirect(string path, string userCode, string userName, string? deviceIp, string? username, string? password)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Json(new { success = false, message = "يرجى تحديد مسار قاعدة بيانات ZKTeco." });
            }
            if (string.IsNullOrWhiteSpace(userCode) || string.IsNullOrWhiteSpace(userName))
            {
                return Json(new { success = false, message = "يرجى إدخال كود واسم الموظف." });
            }

            (username, password) = await ResolveCredentialsAsync(path, username, password);

            var (success, msg) = await _zkAccessService.AddOrUpdateUserInAccessDbAsync(path, userCode.Trim(), userName.Trim(), deviceIp, username, password);
            return Json(new { success, message = msg });
        }

        [HttpGet]
        public async Task<IActionResult> GetZkUsers(string path, string? search, string? username, string? password)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                return Json(new { success = false, message = "المسار غير محدد." });
            }

            (username, password) = await ResolveCredentialsAsync(path, username, password);

            var users = await _zkAccessService.GetUsersAsync(path, search, limit: 100, username: username, password: password);
            return Json(new { success = true, count = users.Count, users });
        }
    }
}
