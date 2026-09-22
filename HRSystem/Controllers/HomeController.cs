using ClosedXML.Excel;
using System.IO;
using HRSystem.Models;
using HRSystem.ModelView;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Diagnostics;
using MiniExcelLibs;
using System.IO;
using zkemkeeper;
using Microsoft.Extensions.Logging;

using Microsoft.AspNetCore.Authorization;

namespace HRSystem.Controllers
{
    [Authorize]
    public class HomeController : Controller
    {
        private readonly HRContext _context;
        private readonly ILogger<HomeController> _logger;
        private readonly UserManager<ApplicationUser> _userManager;

        public HomeController(HRContext context, ILogger<HomeController> logger, UserManager<ApplicationUser> userManager)
        {
            _context = context;
            _logger = logger;
            _userManager = userManager;
        }


        public async Task<IActionResult> Index(int? branchId, int? deviceId, DateTime? dateFrom, DateTime? dateTo, string searchTerm, bool calculate = false, int page = 1)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (User.IsInRole("BranchAdmin") && currentUser?.BranchId != null)
            {
                branchId = currentUser.BranchId;
            }
            else if (User.IsInRole("SuperAdmin") || User.IsInRole("HR"))
            {
                // Let them use the branchId from the filter dropdown
            }
            else
            {
                // Default fallback for unauthorized or other roles if any
                return Challenge();
            }
            // 1. زيادة وقت المهلة لتجنب الـ Timeout في الجداول الكبيرة
            _context.Database.SetCommandTimeout(120);

            // 2. تحديد مدى زمني افتراضي (اليوم فقط) لفتح الصفحة بسرعة فائقة
            if (!dateFrom.HasValue && !dateTo.HasValue)
            {
                dateFrom = DateTime.Now.Date;
                dateTo = DateTime.Now.Date;
            }

            // تصحيح التواريخ لو المستخدم عكسهم
            if (dateFrom.HasValue && dateTo.HasValue && dateFrom > dateTo)
            {
                var temp = dateFrom;
                dateFrom = dateTo;
                dateTo = temp;
            }

            int pageSize = 100; // حجم الصفحة المثالي
            var baseQuery = _context.Attendances.AsNoTracking();

            // تحسين الفلترة بالفرع لتجنب الـ Join المتكرر
            if (branchId.HasValue && branchId.Value > 0)
            {
                var deviceIdsInBranch = await _context.FingerDevices
                    .AsNoTracking()
                    .Where(d => d.BranchId == branchId.Value)
                    .Select(d => d.DeviceId)
                    .ToListAsync();
                baseQuery = baseQuery.Where(l => deviceIdsInBranch.Contains(l.DeviceId));
            }

            if (deviceId.HasValue && deviceId.Value > 0)
                baseQuery = baseQuery.Where(l => l.DeviceId == deviceId.Value);

            if (dateFrom.HasValue)
            {
                var fromDate = dateFrom.Value.Date;
                baseQuery = baseQuery.Where(l => l.CheckTime >= fromDate);
            }

            if (dateTo.HasValue)
            {
                var toDate = dateTo.Value.Date.AddDays(1);
                baseQuery = baseQuery.Where(l => l.CheckTime < toDate);
            }

            if (!string.IsNullOrEmpty(searchTerm))
            {
                var matchingEmpCodes = await ResolveEmployeeCodesAsync(searchTerm);
                if (matchingEmpCodes.Any())
                {
                    baseQuery = baseQuery.Where(l => matchingEmpCodes.Contains(l.EmployeeCode));
                }
                else
                {
                    baseQuery = baseQuery.Where(l => false);
                }
            }

            // 1. استعلام العد السريع (فقط إذا كان الجدول ضخم)
            int totalCount = await baseQuery.Select(l => l.AttendanceId).CountAsync();
            int totalPages = (int)Math.Ceiling(totalCount / (double)pageSize);
            if (page < 1) page = 1;

            // 2. جلب البيانات الخام
            var rawLogs = await baseQuery
                .OrderByDescending(l => l.CheckTime)
                .Skip((page - 1) * pageSize)
                .Take(pageSize)
                .Select(l => new 
                {
                    l.EmployeeCode,
                    l.DeviceId,
                    l.CheckTime,
                    l.CheckType
                })
                .ToListAsync();

            // 3. جلب البيانات المرتبطة لهؤلاء الـ 100 موظف/جهاز فقط (Batch Loading)
            var empCodes = rawLogs.Select(x => x.EmployeeCode).Distinct().ToList();
            var deviceIds = rawLogs.Select(x => x.DeviceId).Distinct().ToList();

            var empList = await _context.Employees.AsNoTracking()
                .Where(e => empCodes.Contains(e.No))
                .Select(e => new { e.No, e.SearchName, e.start_shift, e.end_shift })
                .ToListAsync();

            var employees = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var shiftStartMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            var shiftEndMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            foreach (var emp in empList)
            {
                if (!string.IsNullOrEmpty(emp.No) && !employees.ContainsKey(emp.No))
                {
                    employees[emp.No] = emp.SearchName;
                    if (!string.IsNullOrEmpty(emp.start_shift)) shiftStartMap[emp.No] = emp.start_shift;
                    if (!string.IsNullOrEmpty(emp.end_shift)) shiftEndMap[emp.No] = emp.end_shift;
                }
            }

            var devList = await _context.FingerDevices.AsNoTracking()
                .Include(d => d.Branch)
                .Where(d => deviceIds.Contains(d.DeviceId))
                .ToListAsync();

            var devices = new Dictionary<int, string>();
            var branchMap = new Dictionary<int, string>();
            foreach (var dev in devList)
            {
                devices[dev.DeviceId] = dev.DeviceName;
                branchMap[dev.DeviceId] = dev.Branch != null ? dev.Branch.BranchName : "بدون فرع";
            }

            // 4. تجميع البيانات النهائية في الذاكرة (Memory Assembly)
            var logs = rawLogs.Select(l => new AttendanceVM
            {
                EmployeeNo = l.EmployeeCode,
                SearchName = employees.TryGetValue(l.EmployeeCode, out var empName) ? empName : "موظف غير مسجل",
                BranchName = branchMap.TryGetValue(l.DeviceId, out var bName) ? bName : "بدون فرع",
                DeviceName = devices.TryGetValue(l.DeviceId, out var dName) ? dName : "جهاز غير معروف",
                LogTime = l.CheckTime,
                Direction = l.CheckType == "In" ? "دخول" : "خروج"
            }).ToList();

            if (calculate)
            {
                foreach (var log in logs)
                {
                    if (shiftStartMap.TryGetValue(log.EmployeeNo, out var sStart) && shiftEndMap.TryGetValue(log.EmployeeNo, out var sEnd))
                    {
                        log.StartShift = sStart;
                        log.EndShift = sEnd;
                        log.IsCalculated = true;

                        try
                        {
                            if (string.IsNullOrEmpty(sStart) || string.IsNullOrEmpty(sEnd)) continue;

                            DateTime logDate = log.LogTime.Date;
                            DateTime shiftStart = DateTime.Parse(logDate.ToShortDateString() + " " + sStart);
                            DateTime shiftEnd = DateTime.Parse(logDate.ToShortDateString() + " " + sEnd);

                            // معالجة الشيفتات المسائية (مثلاً من 10 م إلى 6 ص)
                            if (shiftEnd < shiftStart) shiftEnd = shiftEnd.AddDays(1);

                            // 1. لو البصمة بعد نهاية الشيفت -> نحسب إضافي (حتى لو الجهاز قال "دخول")
                            if (log.LogTime > shiftEnd)
                            {
                                var overtime = (log.LogTime - shiftEnd).TotalMinutes;
                                log.OvertimeMinutes = overtime;
                                log.DelayMinutes = 0; // نلغي أي تأخير تم حسابه بالخطأ
                            }
                            // 2. لو البصمة بعد بداية الشيفت وقبل نهايته -> نحسب تأخير (فقط لو كانت "دخول")
                            else if (log.LogTime > shiftStart && log.Direction == "دخول")
                            {
                                var delay = (log.LogTime - shiftStart).TotalMinutes;
                                log.DelayMinutes = delay;
                                log.OvertimeMinutes = 0;
                            }
                        }
                        catch { }
                    }
                }
            }

            // 5. جلب بيانات الفلترة
            var devicesForFilter = await _context.FingerDevices.AsNoTracking()
                .Where(d => !branchId.HasValue || branchId.Value <= 0 || d.BranchId == branchId.Value)
                .ToListAsync();
            
            ViewBag.Devices = devicesForFilter;
            ViewBag.Branches = await _context.Branches.AsNoTracking().ToListAsync();

            ViewBag.SelectedBranchId = branchId;
            ViewBag.SelectedDeviceId = deviceId;
            ViewBag.SearchTerm = searchTerm;
            ViewBag.DateFrom = dateFrom?.ToString("yyyy-MM-dd");
            ViewBag.DateTo = dateTo?.ToString("yyyy-MM-dd");
            ViewBag.CurrentPage = page;
            ViewBag.TotalPages = totalPages;
            ViewBag.Calculate = calculate;

            return View(logs);
        }

        [HttpGet]
        public async Task<IActionResult> GetDeviceStats(DateTime? dateFrom, DateTime? dateTo, int? branchId)
        {
            _context.Database.SetCommandTimeout(120);
            var currentUser = await _userManager.GetUserAsync(User);
            
            if (User.IsInRole("BranchAdmin") && currentUser?.BranchId != null)
            {
                branchId = currentUser.BranchId;
            }
            else if (!User.IsInRole("SuperAdmin") && !User.IsInRole("HR"))
            {
                return Challenge();
            }

            if (!dateFrom.HasValue && !dateTo.HasValue)
            {
                dateFrom = DateTime.Now.Date;
                dateTo = DateTime.Now.Date;
            }

            if (dateFrom.HasValue && dateTo.HasValue && dateFrom > dateTo)
            {
                var temp = dateFrom;
                dateFrom = dateTo;
                dateTo = temp;
            }

            // تحسين فائق للأداء: بدلاً من عمل Distinct على ملايين السجلات في جدول الحضور
            // سنقوم بالتحقق من وجود بصمات لكل جهاز مباشرة (Semi-join)
            var activeDevicesQuery = _context.FingerDevices.AsNoTracking().Where(d => d.IsActive == true);
            
            if (branchId.HasValue && branchId.Value > 0)
                activeDevicesQuery = activeDevicesQuery.Where(d => d.BranchId == branchId.Value);

            // نحدد الأجهزة التي لها بصمة واحدة على الأقل في الفترة المختارة
            var syncedDeviceIds = await activeDevicesQuery
                .Where(d => d.Attendances.Any(a => a.CheckTime >= dateFrom.Value.Date && a.CheckTime < dateTo.Value.Date.AddDays(1)))
                .Select(d => d.DeviceId)
                .ToListAsync();
            
            var totalDevices = await activeDevicesQuery.CountAsync();

            var missingDevices = await activeDevicesQuery
                .Where(d => !syncedDeviceIds.Contains(d.DeviceId))
                .Select(d => new { id = d.DeviceId, name = d.DeviceName })
                .ToListAsync();

            return Json(new
            {
                syncedCount = syncedDeviceIds.Count,
                totalDevices = totalDevices,
                missingDevices = missingDevices
            });
        }
        public async Task<IActionResult> Export(int? branchId, int? deviceId, DateTime? dateFrom, DateTime? dateTo, string searchTerm, bool calculate = false)
        {
            if (!User.Identity.IsAuthenticated) return Challenge();

            var currentUser = await _userManager.GetUserAsync(User);
            if (User.IsInRole("BranchAdmin") && currentUser?.BranchId != null)
            {
                branchId = currentUser.BranchId;
            }
            else if (User.IsInRole("SuperAdmin") || User.IsInRole("HR"))
            {
                // Access allowed
            }
            else
            {
                return Forbid();
            }

            var query = from att in _context.Attendances
                        join emp in _context.Employees on att.EmployeeCode equals emp.No into employees
                        from emp in employees.DefaultIfEmpty()
                        join dev in _context.FingerDevices on att.DeviceId equals dev.DeviceId
                        join br in _context.Branches on dev.BranchId equals br.BranchId into branches
                        from br in branches.DefaultIfEmpty()
                        select new { att, emp, dev, br };

            if (branchId.HasValue && branchId.Value > 0)
                query = query.Where(x => x.dev.BranchId == branchId.Value);

            if (deviceId.HasValue && deviceId.Value > 0)
                query = query.Where(x => x.att.DeviceId == deviceId.Value);

            if (dateFrom.HasValue)
                query = query.Where(x => x.att.CheckTime >= dateFrom.Value.Date);

            if (dateTo.HasValue)
                query = query.Where(x => x.att.CheckTime < dateTo.Value.Date.AddDays(1));

            if (!string.IsNullOrWhiteSpace(searchTerm))
            {
                var matchingEmpCodes = await ResolveEmployeeCodesAsync(searchTerm);
                if (matchingEmpCodes.Any())
                {
                    query = query.Where(x => matchingEmpCodes.Contains(x.att.EmployeeCode));
                }
                else
                {
                    query = query.Where(x => false);
                }
            }

            var rawLogs = await query
                .OrderByDescending(x => x.att.CheckTime)
                .Select(x => new 
                {
                    EmployeeNo = x.att.EmployeeCode,
                    SearchName = x.emp != null ? x.emp.SearchName : "غير مسجل",
                    BranchName = x.br != null ? x.br.BranchName : "بدون فرع",
                    DeviceName = x.dev.DeviceName,
                    CheckTime = x.att.CheckTime,
                    Direction = x.att.CheckType == "In" ? "دخول" : "خروج",
                    StartShift = x.emp != null ? x.emp.start_shift : null,
                    EndShift = x.emp != null ? x.emp.end_shift : null
                })
                .ToListAsync();

            if (calculate)
            {
                var processedLogs = rawLogs.Select(x => {
                    double delayMins = 0;
                    double overtimeMins = 0;

                    if (!string.IsNullOrEmpty(x.StartShift) && !string.IsNullOrEmpty(x.EndShift))
                    {
                        try
                        {
                            DateTime logDate = x.CheckTime.Date;
                            DateTime shiftStart = DateTime.Parse(logDate.ToShortDateString() + " " + x.StartShift);
                            DateTime shiftEnd = DateTime.Parse(logDate.ToShortDateString() + " " + x.EndShift);

                            if (shiftEnd < shiftStart) shiftEnd = shiftEnd.AddDays(1);

                            if (x.CheckTime > shiftEnd)
                            {
                                var overtime = (x.CheckTime - shiftEnd).TotalMinutes;
                                overtimeMins = overtime;
                                delayMins = 0;
                            }
                            else if (x.CheckTime > shiftStart && x.Direction == "دخول")
                            {
                                var delay = (x.CheckTime - shiftStart).TotalMinutes;
                                delayMins = delay;
                                overtimeMins = 0;
                            }
                        }
                        catch { }
                    }

                    return new
                    {
                        كود_الموظف = x.EmployeeNo,
                        اسم_الموظف = x.SearchName,
                        الفرع = x.BranchName,
                        اسم_الجهاز = x.DeviceName,
                        الوقت_والتاريخ = x.CheckTime.ToString("yyyy-MM-dd hh:mm:ss tt"),
                        الحالة = x.Direction,
                        الشيفت = (x.StartShift ?? "") + " - " + (x.EndShift ?? ""),
                        التاخير_بالدقايق = Math.Round(delayMins, 2),
                        الاوفر_تايم_بالدقايق = Math.Round(overtimeMins, 2)
                    };
                }).ToList();

                var memoryStreamCalc = new MemoryStream();
                memoryStreamCalc.SaveAs(processedLogs);
                memoryStreamCalc.Seek(0, SeekOrigin.Begin);
                return File(memoryStreamCalc, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"HR_Attendance_Report_{DateTime.Now:yyyyMMdd}.xlsx");
            }

            var finalLogs = rawLogs.Select(x => new
            {
                كود_الموظف = x.EmployeeNo,
                اسم_الموظف = x.SearchName,
                الفرع = x.BranchName,
                اسم_الجهاز = x.DeviceName,
                الوقت_والتاريخ = x.CheckTime.ToString("yyyy-MM-dd hh:mm:ss tt"),
                الحالة = x.Direction
            }).ToList();

            var memoryStream = new MemoryStream();
            memoryStream.SaveAs(finalLogs);
            memoryStream.Seek(0, SeekOrigin.Begin);
            return File(memoryStream, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", $"Basta_Logs_{DateTime.Now:yyyyMMdd_HHmmss}.xlsx");
        }

        // Returns latest attendance records as JSON. If `since` provided, returns records with CheckTime > since.
        [HttpGet]
        public async Task<IActionResult> GetLatest(DateTime? since, int? branchId, int? deviceId, string date)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (User.IsInRole("BranchAdmin") && currentUser?.BranchId != null)
            {
                branchId = currentUser.BranchId;
            }
            else if (User.IsInRole("SuperAdmin") || User.IsInRole("HR"))
            {
                // Let them use the branchId from the filter dropdown
            }
            else
            {
                // Default fallback for unauthorized or other roles if any
                return Challenge();
            }
            var query = _context.Attendances
                .Include(l => l.Device)
                .ThenInclude(d => d.Branch)
                .AsQueryable();

            if (branchId.HasValue && branchId.Value > 0)
                query = query.Where(l => l.Device.BranchId == branchId.Value);

            if (deviceId.HasValue && deviceId.Value > 0)
                query = query.Where(l => l.DeviceId == deviceId.Value);

            if (!string.IsNullOrEmpty(date) && DateTime.TryParse(date, out var searchDate))
            {
                query = query.Where(l => l.CheckTime.Date == searchDate.Date);
            }

            if (since.HasValue)
            {
                query = query.Where(l => l.CheckTime > since.Value);
            }

            var logs = await query
                .OrderByDescending(l => l.CheckTime)
                .Take(50)
                .Select(l => new AttendanceVM
                {
                    EmployeeNo = l.EmployeeCode,
                    SearchName = _context.Employees
                                    .Where(e => e.No == l.EmployeeCode)
                                    .Select(e => e.SearchName)
                                    .FirstOrDefault() ?? "موظف غير مسجل",
                    BranchName = l.Device.Branch.BranchName,
                    DeviceName = l.Device.DeviceName,
                    LogTime = l.CheckTime,
                    Direction = l.CheckType == "In" ? "دخول" : "خروج"
                })
                .ToListAsync();

            return Json(logs);
        }

        // Trigger an immediate pull from devices (for branch/device filters) then return latest rows
        [HttpGet]
        public async Task<IActionResult> PullAndGetLatest(DateTime? since, int? branchId, int? deviceId, string date)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (User.IsInRole("BranchAdmin") && currentUser?.BranchId != null)
            {
                branchId = currentUser.BranchId;
            }
            else if (User.IsInRole("SuperAdmin") || User.IsInRole("HR"))
            {
                // Let them use the branchId from the filter dropdown
            }
            else
            {
                // Default fallback for unauthorized or other roles if any
                return Challenge();
            }
            try
            {
                // select devices to sync
                var devicesQuery = _context.FingerDevices.Where(d => d.IsActive == true).AsQueryable();
                if (branchId.HasValue && branchId.Value > 0)
                    devicesQuery = devicesQuery.Where(d => d.BranchId == branchId.Value);
                if (deviceId.HasValue && deviceId.Value > 0)
                    devicesQuery = devicesQuery.Where(d => d.DeviceId == deviceId.Value);

                var devices = await devicesQuery.ToListAsync();

                // cache valid employee mapping for flexible lookup
                var employeesList = await _context.Employees.AsNoTracking().Select(e => e.No).ToListAsync();
                var employeeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                foreach (var empNo in employeesList)
                {
                    var trimmed = empNo.Trim();
                    if (!employeeMap.ContainsKey(trimmed))
                        employeeMap[trimmed] = empNo;
                    
                    var normalized = trimmed.TrimStart('0');
                    if (!string.IsNullOrEmpty(normalized) && !employeeMap.ContainsKey(normalized))
                        employeeMap[normalized] = empNo;
                }

                foreach (var deviceRecord in devices)
                {
                    if (string.IsNullOrEmpty(deviceRecord.Ipaddress))
                    {
                        _logger?.LogWarning("Device {Device} has no IP", deviceRecord.DeviceName);
                        continue;
                    }

                    try
                    {
                        CZKEM device = new CZKEM();
                        int port = deviceRecord.Port ?? 4370;
                        _logger?.LogInformation("Attempting connect to {Ip}:{Port}", deviceRecord.Ipaddress, port);
                        if (device.Connect_Net(deviceRecord.Ipaddress, port))
                        {
                            device.EnableDevice(1, false);
                            if (device.ReadGeneralLogData(1))
                            {
                                string enrollNumber = "";
                                int verifyMode = 0, inOutMode = 0, year = 0, month = 0, day = 0, hour = 0, minute = 0, second = 0, workCode = 0;
                                int count = 0, newLogsCount = 0;

                                // only consider logs after last sync to avoid reading whole history every time
                                var lastSync = deviceRecord.LastSyncTime ?? new DateTime(1900, 1, 1);
                                var fetchStart = lastSync > new DateTime(1900, 1, 1) ? lastSync.AddDays(-1) : lastSync;

                                // pre-load existing keys for this device after fetchStart to avoid per-row DB queries
                                var existingKeys = await _context.Attendances
                                    .Where(a => a.DeviceId == deviceRecord.DeviceId && a.CheckTime >= fetchStart)
                                    .Select(a => a.EmployeeCode + "|" + a.CheckTime.Ticks)
                                    .ToListAsync();
                                var existingSet = new HashSet<string>(existingKeys);

                                while (device.SSR_GetGeneralLogData(1, out enrollNumber, out verifyMode, out inOutMode,
                                       out year, out month, out day, out hour, out minute, out second, ref workCode))
                                {
                                    count++;
                                    enrollNumber = enrollNumber.Trim();
                                    DateTime logDate;
                                    try { logDate = new DateTime(year, month, day, hour, minute, second); }
                                    catch { continue; }

                                    // skip old logs (already synced)
                                    if (logDate <= fetchStart) continue;

                                    string exactEmployeeCode = null;
                                    if (employeeMap.TryGetValue(enrollNumber, out var code))
                                    {
                                        exactEmployeeCode = code;
                                    }
                                    else if (employeeMap.TryGetValue(enrollNumber.TrimStart('0'), out var code2))
                                    {
                                        exactEmployeeCode = code2;
                                    }

                                    if (exactEmployeeCode == null)
                                    {
                                        string empName = "";
                                        string password = "";
                                        int privilege = 0;
                                        bool enabled = false;
                                        if (device.SSR_GetUserInfo(1, enrollNumber, out empName, out password, out privilege, out enabled) && !string.IsNullOrEmpty(empName))
                                        {
                                            empName = empName.Trim('\0', ' ', '\r', '\n');
                                            empName = HRSystem.Services.ZKTecoHelper.DecodeFromZKTeco(empName);
                                        }
                                        
                                        if (string.IsNullOrEmpty(empName))
                                        {
                                            empName = "موظف غير مسجل " + enrollNumber;
                                        }

                                        var newEmp = new Employee
                                        {
                                            No = enrollNumber,
                                            SearchName = empName,
                                            BranchId = deviceRecord.BranchId,
                                            Disabled = false,
                                            UpdatedAt = DateTime.Now
                                        };

                                        _context.Employees.Add(newEmp);
                                        employeeMap[enrollNumber] = enrollNumber;
                                        exactEmployeeCode = enrollNumber;
                                    }

                                    var key = exactEmployeeCode + "|" + logDate.Ticks;
                                    if (existingSet.Contains(key)) continue;

                                    _context.Attendances.Add(new Attendance
                                    {
                                        EmployeeCode = exactEmployeeCode,
                                        CheckTime = logDate,
                                        CheckType = inOutMode == 0 ? "In" : "Out",
                                        DeviceId = deviceRecord.DeviceId,
                                        VerifyMode = verifyMode
                                    });
                                    existingSet.Add(key);
                                    newLogsCount++;
                                }

                                if (newLogsCount > 0)
                                {
                                    await _context.SaveChangesAsync();
                                }
                                deviceRecord.LastSyncTime = DateTime.Now;
                                await _context.SaveChangesAsync();
                                _logger?.LogInformation("Pulled {Count} logs from {Device} ({New})", count, deviceRecord.DeviceName, newLogsCount);
                            }
                            device.EnableDevice(1, true);
                            device.Disconnect();
                        }
                        else
                        {
                            _logger?.LogWarning("Failed connect to {Device}", deviceRecord.DeviceName);
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger?.LogError(ex, "Error pulling device {Device}", deviceRecord.DeviceName);
                    }
                }
            }
            catch (Exception ex)
            {
                _logger?.LogError(ex, "PullAndGetLatest failed");
            }

            // after pull, return latest from DB (like GetLatest)
            var query = _context.Attendances
                .Include(l => l.Device)
                .ThenInclude(d => d.Branch)
                .AsQueryable();

            if (branchId.HasValue && branchId.Value > 0)
                query = query.Where(l => l.Device.BranchId == branchId.Value);

            if (deviceId.HasValue && deviceId.Value > 0)
                query = query.Where(l => l.DeviceId == deviceId.Value);

            if (!string.IsNullOrEmpty(date) && DateTime.TryParse(date, out var searchDate))
            {
                query = query.Where(l => l.CheckTime.Date == searchDate.Date);
            }

            if (since.HasValue)
            {
                query = query.Where(l => l.CheckTime > since.Value);
            }

            var logs = await query
                .OrderByDescending(l => l.CheckTime)
                .Take(50)
                .Select(l => new AttendanceVM
                {
                    EmployeeNo = l.EmployeeCode,
                    SearchName = _context.Employees
                                    .Where(e => e.No == l.EmployeeCode)
                                    .Select(e => e.SearchName)
                                    .FirstOrDefault() ?? "موظف غير مسجل",
                    BranchName = l.Device.Branch.BranchName,
                    DeviceName = l.Device.DeviceName,
                    LogTime = l.CheckTime,
                    Direction = l.CheckType == "In" ? "دخول" : "خروج"
                })
                .ToListAsync();

            return Json(logs);
        }

        [Authorize(Roles = "SuperAdmin")]
        public async Task<IActionResult> SessionLogs()
        {
            var logs = await _context.UserSessionLogs
                .OrderByDescending(l => l.AccessTime)
                .Take(500)
                .ToListAsync();
            return View(logs);
        }

        public IActionResult Privacy()
        {
            return View();
        }

        [ResponseCache(Duration = 0, Location = ResponseCacheLocation.None, NoStore = true)]
        public IActionResult Error()
        {
            return View();
        }

        [HttpGet]
        [AllowAnonymous]
        public IActionResult GetAutoSyncStatus()
        {
            return Json(new
            {
                lastSyncTime = HRSystem.Services.AttendanceService.LastAutoSyncTime?.ToString("yyyy/MM/dd hh:mm:ss tt"),
                syncedCount = HRSystem.Services.AttendanceService.LastSyncedDeviceCount,
                totalCount = HRSystem.Services.AttendanceService.TotalActiveDeviceCount,
                status = HRSystem.Services.AttendanceService.LastSyncStatus
            });
        }

        private async Task<List<string>> ResolveEmployeeCodesAsync(string searchTerm)
        {
            if (string.IsNullOrWhiteSpace(searchTerm)) return new List<string>();

            // التقسيم حسب الفواصل الإنجليزية والعربية والمنقوطة والأسطر
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
                // لو لم توجد فواصل صريحة ولكن أرقام مفصولة بمسافات (مثل "101 102 103")
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

            var matchingCodes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            foreach (var rawTerm in tokens)
            {
                var term = rawTerm.Trim();
                if (string.IsNullOrEmpty(term)) continue;

                matchingCodes.Add(term);
                var unpadded = term.TrimStart('0');
                if (!string.IsNullOrEmpty(unpadded))
                {
                    matchingCodes.Add(unpadded);
                    matchingCodes.Add(unpadded.PadLeft(4, '0'));
                    matchingCodes.Add(unpadded.PadLeft(5, '0'));
                }

                bool isNumeric = term.All(char.IsDigit);

                if (isNumeric)
                {
                    var foundEmpCodes = await _context.Employees
                        .AsNoTracking()
                        .Where(e => e.No == term || 
                                    (unpadded != "" && e.No == unpadded) || 
                                    e.SearchName.Contains(term))
                        .Select(e => e.No)
                        .ToListAsync();

                    foreach (var code in foundEmpCodes)
                    {
                        matchingCodes.Add(code);
                    }
                }
                else
                {
                    var foundEmpCodes = await _context.Employees
                        .AsNoTracking()
                        .Where(e => e.SearchName.Contains(term) || (e.EnglishName != null && e.EnglishName.Contains(term)))
                        .Select(e => e.No)
                        .ToListAsync();

                    foreach (var code in foundEmpCodes)
                    {
                        matchingCodes.Add(code);
                    }
                }
            }

            return matchingCodes.ToList();
        }
    }
}
