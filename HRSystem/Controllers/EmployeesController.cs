using HRSystem.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using MiniExcelLibs;
using System.Collections.Generic;
using zkemkeeper;

using Microsoft.AspNetCore.Authorization;

namespace HRSystem.Controllers
{
    [Authorize]
    public class EmployeesController : Controller
    {
        private readonly HRContext _context;
        private readonly UserManager<ApplicationUser> _userManager;

        public EmployeesController(HRContext context, UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _userManager = userManager;
        }

        public async Task<IActionResult> Index(string searchTerm)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            var query = _context.Employees.Include(e => e.Branch).AsQueryable();

            if (User.IsInRole("BranchAdmin") && currentUser?.BranchId != null)
            {
                query = query.Where(e => e.BranchId == currentUser.BranchId);
            }

            if (!string.IsNullOrEmpty(searchTerm))
            {
                char[] primaryDelimiters = new[] { ',', '،', ';', '\r', '\n', '\t' };
                List<string> tokens;

                if (searchTerm.IndexOfAny(primaryDelimiters) >= 0)
                {
                    tokens = searchTerm.Split(primaryDelimiters, StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim())
                        .Where(t => !string.IsNullOrEmpty(t))
                        .Distinct(StringComparer.OrdinalIgnoreCase)
                        .ToList();
                }
                else
                {
                    var spaceTokens = searchTerm.Split(' ', StringSplitOptions.RemoveEmptyEntries)
                        .Select(t => t.Trim())
                        .Where(t => !string.IsNullOrEmpty(t))
                        .ToList();

                    if (spaceTokens.Count > 1 && spaceTokens.All(t => t.All(char.IsDigit)))
                    {
                        tokens = spaceTokens.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
                    }
                    else
                    {
                        tokens = new List<string> { searchTerm.Trim() };
                    }
                }

                var matchingEmpIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var t in tokens)
                {
                    matchingEmpIds.Add(t);
                    var unpadded = t.TrimStart('0');
                    if (!string.IsNullOrEmpty(unpadded))
                    {
                        matchingEmpIds.Add(unpadded);
                        matchingEmpIds.Add(unpadded.PadLeft(4, '0'));
                        matchingEmpIds.Add(unpadded.PadLeft(5, '0'));
                    }

                    if (t.All(char.IsDigit))
                    {
                        var ids = await _context.Employees.AsNoTracking()
                            .Where(e => e.No == t || (unpadded != "" && e.No == unpadded) || e.SearchName.Contains(t))
                            .Select(e => e.No)
                            .ToListAsync();
                        foreach (var id in ids) matchingEmpIds.Add(id);
                    }
                    else
                    {
                        var ids = await _context.Employees.AsNoTracking()
                            .Where(e => e.SearchName.Contains(t) || (e.EnglishName != null && e.EnglishName.Contains(t)))
                            .Select(e => e.No)
                            .ToListAsync();
                        foreach (var id in ids) matchingEmpIds.Add(id);
                    }
                }

                var matchingList = matchingEmpIds.ToList();
                query = query.Where(e => matchingList.Contains(e.No));
            }

            var employees = await query.OrderByDescending(e => e.UpdatedAt ?? DateTime.MinValue).ToListAsync();
            ViewBag.SearchTerm = searchTerm;
            ViewBag.Branches = await _context.Branches.Where(b => b.IsActive == true).ToListAsync();
            return View(employees);
        }

        public async Task<IActionResult> Details(string id)
        {
            if (string.IsNullOrEmpty(id)) return NotFound();

            var employee = await _context.Employees
                .Include(e => e.Branch)
                .FirstOrDefaultAsync(m => m.No == id);

            if (employee == null) return NotFound();

            return View(employee);
        }

        public async Task<IActionResult> Edit(string id)
        {
            if (string.IsNullOrEmpty(id)) return NotFound();

            var employee = await _context.Employees.FirstOrDefaultAsync(e => e.No == id);
            if (employee == null) return NotFound();

            ViewBag.Branches = await _context.Branches.ToListAsync();
            return View(employee);
        }

        [HttpPost]
        [ValidateAntiForgeryToken]
        public async Task<IActionResult> Edit(string id, Employee employee, IFormFile photoFile)
        {
            if (id != employee.No) return NotFound();

            try
            {
                var existingEmployee = await _context.Employees.AsNoTracking().FirstOrDefaultAsync(e => e.No == id);
                if (existingEmployee == null) return NotFound();

                if (photoFile != null && photoFile.Length > 0)
                {
                    var uploadsFolder = Path.Combine(Directory.GetCurrentDirectory(), "wwwroot", "uploads", "employees");
                    if (!Directory.Exists(uploadsFolder)) Directory.CreateDirectory(uploadsFolder);

                    var uniqueFileName = Guid.NewGuid().ToString() + "_" + photoFile.FileName;
                    var filePath = Path.Combine(uploadsFolder, uniqueFileName);

                    using (var fileStream = new FileStream(filePath, FileMode.Create))
                    {
                        await photoFile.CopyToAsync(fileStream);
                    }

                    employee.Photo = "/uploads/employees/" + uniqueFileName;
                }
                else
                {
                    employee.Photo = existingEmployee.Photo;
                }

                employee.UpdatedAt = DateTime.Now;
                _context.Update(employee);
                await _context.SaveChangesAsync();
                
                // مزامنة ودفع الاسم الجديد تلقائياً لأجهزة البصمة إذا تم تغيير الاسم
                if (existingEmployee.SearchName != employee.SearchName && !string.IsNullOrEmpty(employee.SearchName))
                {
                    var empNo = employee.No;
                    var newSearchName = employee.SearchName;
                    var targetBranchId = employee.BranchId;

                    _ = Task.Run(async () => {
                        try
                        {
                            using (var scope = HttpContext.RequestServices.CreateScope())
                            {
                                var dbContext = scope.ServiceProvider.GetRequiredService<HRContext>();
                                var devices = await dbContext.FingerDevices
                                    .Where(d => d.IsActive == true && (targetBranchId == null || d.BranchId == targetBranchId))
                                    .ToListAsync();

                                foreach (var dev in devices)
                                {
                                    try
                                    {
                                        CZKEM zk = new CZKEM();
                                        if (zk.Connect_Net(dev.Ipaddress, dev.Port ?? 4370))
                                        {
                                            string encName = HRSystem.Services.ZKTecoHelper.EncodeForZKTeco(newSearchName);
                                            zk.SSR_SetUserInfo(1, empNo, encName, "", 0, true);
                                            zk.RefreshData(1);
                                            zk.Disconnect();
                                        }
                                    }
                                    catch { }
                                }
                            }
                        }
                        catch { }
                    });
                }

                TempData["Success"] = "تم تحديث بيانات الموظف بنجاح وتسميع الاسم على أجهزة البصمة.";
                return RedirectToAction(nameof(Index));
            }
            catch (Exception ex)
            {
                ModelState.AddModelError("", "حدث خطأ أثناء الحفظ: " + ex.Message);
            }

            ViewBag.Branches = await _context.Branches.ToListAsync();
            return View(employee);
        }

        [HttpPost]
        public async Task<IActionResult> Delete(string id)
        {
            var employee = await _context.Employees.FirstOrDefaultAsync(e => e.No == id);
            if (employee == null) return Json(new { success = false, message = "الموظف غير موجود." });

            try
            {
                _context.Employees.Remove(employee);
                await _context.SaveChangesAsync();
                return Json(new { success = true, message = "تم حذف الموظف بنجاح." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "فشل الحذف: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> Import(IFormFile excelFile)
        {
            if (excelFile == null || excelFile.Length <= 0)
            {
                TempData["Error"] = "الرجاء اختيار ملف صالح.";
                return RedirectToAction("Index");
            }

            try
            {
                var filePath = Path.GetTempFileName();
                using (var stream = System.IO.File.Create(filePath))
                {
                    await excelFile.CopyToAsync(stream);
                }

                using (var stream = System.IO.File.OpenRead(filePath))
                {
                    var rows = stream.Query(useHeaderRow: true).ToList();
                    
                    if (rows.Any())
                    {
                        var branchIds = await _context.Branches.Select(b => b.BranchId).ToListAsync();
                        int defaultBranchId = branchIds.FirstOrDefault();
                        int savedCount = 0;
                        var employeeProperties = typeof(Employee).GetProperties();

                        string detectedHeaders = "";
                        string firstError = "";
                        foreach (IDictionary<string, object> row in rows)
                        {
                            try
                            {
                                // تنظيف أسماء الأعمدة: تحويلها لصغير، حذف المسافات، وحذف النقطة (لتحويل "No." إلى "no")
                                var normalizedRow = row.ToDictionary(
                                    k => k.Key.Replace(" ", "").Replace(".", "").Trim().ToLower(), 
                                    v => v.Value);

                                // 1. تحديد كود الموظف
                                string empNo = null;
                                if (normalizedRow.ContainsKey("no")) empNo = normalizedRow["no"]?.ToString();
                                else if (normalizedRow.ContainsKey("employeecode")) empNo = normalizedRow["employeecode"]?.ToString();

                                if (string.IsNullOrEmpty(empNo)) continue;
                                empNo = empNo.Trim();

                                var employee = await _context.Employees.FirstOrDefaultAsync(e => e.No == empNo);
                                bool isNew = false;
                                if (employee == null)
                                {
                                    employee = new Employee { No = empNo };
                                    isNew = true;
                                }

                                // 2. مطابقة كافة الحقول
                                foreach (var prop in employeeProperties)
                                {
                                    if (prop.Name == "No" || prop.Name == "Attendances" || prop.Name == "Branch" || prop.Name == "Id") continue;

                                    string pName = prop.Name.ToLower();

                                    if (normalizedRow.ContainsKey(pName) && normalizedRow[pName] != null)
                                    {
                                        try
                                        {
                                            var value = normalizedRow[pName].ToString();
                                            if (string.IsNullOrWhiteSpace(value)) continue;

                                            var targetType = Nullable.GetUnderlyingType(prop.PropertyType) ?? prop.PropertyType;

                                            if (targetType == typeof(DateOnly))
                                            {
                                                if (DateTime.TryParse(value, out DateTime dt))
                                                    prop.SetValue(employee, DateOnly.FromDateTime(dt));
                                            }
                                            else if (targetType == typeof(string))
                                            {
                                                if (value.Length > 250) value = value.Substring(0, 250);
                                                prop.SetValue(employee, value);
                                            }
                                            else if (targetType == typeof(bool))
                                            {
                                                if (bool.TryParse(value, out bool bVal))
                                                    prop.SetValue(employee, bVal);
                                                else
                                                    prop.SetValue(employee, value == "1" || value.ToLower() == "true");
                                            }
                                            else
                                            {
                                                prop.SetValue(employee, Convert.ChangeType(value, targetType));
                                            }
                                        }
                                        catch { }
                                    }
                                }

                                // 3. التحقق من الحقول الإلزامية والفرع (بدون قيمة افتراضية للفرع بناءً على طلبك)
                                if (string.IsNullOrEmpty(employee.SearchName)) 
                                    employee.SearchName = empNo; 

                                if (isNew) _context.Employees.Add(employee);
                                
                                await _context.SaveChangesAsync();
                                savedCount++;
                            }
                            catch (Exception ex)
                            {
                                if (string.IsNullOrEmpty(firstError))
                                    firstError = $"خطأ في الموظف ({row.Values.FirstOrDefault()}): {ex.Message}";
                                
                                _context.ChangeTracker.Clear();
                            }
                        }

                        if (savedCount == 0 && !string.IsNullOrEmpty(firstError))
                        {
                            TempData["Error"] = "لم يتم الحفظ. السبب التقني: " + firstError;
                        }
                        else
                        {
                             TempData["Success"] = $"تمت بنجاح: استيراد {savedCount} موظف. {(savedCount < rows.Count ? " (بعض الصفوف فشلت بسبب بيانات غير سليمة)" : "")}";
                        }
                    }
                }

                System.IO.File.Delete(filePath);
            }
            catch (Exception ex)
            {
                TempData["Error"] = "حدث خطأ أثناء الاستيراد: " + ex.Message;
            }

            return RedirectToAction("Index");
        }
    }
}
