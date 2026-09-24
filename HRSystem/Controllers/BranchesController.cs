using HRSystem.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System.Net.NetworkInformation;
using zkemkeeper;
using MiniExcelLibs;
using System.IO;
using Microsoft.AspNetCore.Authorization;
using ClosedXML.Excel;

namespace HRSystem.Controllers
{
    [Authorize]
    public class BranchesController : Controller
    {
        private readonly HRContext _context;
        private readonly UserManager<ApplicationUser> _userManager;
        private readonly HRSystem.Services.ZkAccessDbService _zkAccessService;

        public BranchesController(HRContext context, UserManager<ApplicationUser> userManager, HRSystem.Services.ZkAccessDbService zkAccessService)
        {
            _context = context;
            _userManager = userManager;
            _zkAccessService = zkAccessService;
        }

        private async Task<bool> CheckDeviceAccess(int deviceId)
        {
            var currentUser = await _userManager.GetUserAsync(User);
            if (User.IsInRole("SuperAdmin") || User.IsInRole("HR")) return true;
            
            if (User.IsInRole("BranchAdmin") && currentUser?.BranchId != null)
            {
                return await _context.FingerDevices.AnyAsync(d => d.DeviceId == deviceId && d.BranchId == currentUser.BranchId);
            }
            return false;
        }

        private string EncodeForZKTeco(string input)
        {
            return HRSystem.Services.ZKTecoHelper.EncodeForZKTeco(input);
        }

        public async Task<IActionResult> Index()
        {
            var currentUser = await _userManager.GetUserAsync(User);
            IQueryable<Branch> query = _context.Branches;
            
            if (User.IsInRole("SuperAdmin") || User.IsInRole("HR"))
            {
                // Full Access
            }
            else if (User.IsInRole("BranchAdmin") && currentUser?.BranchId != null)
            {
                query = query.Where(b => b.BranchId == currentUser.BranchId);
            }
            else
            {
                return Challenge();
            }
            var branches = await query.ToListAsync();
            return View(branches);
        }

        public async Task<IActionResult> FingerDevices()
        {
            var currentUser = await _userManager.GetUserAsync(User);
            IQueryable<FingerDevice> query = _context.FingerDevices.Include(d => d.Branch);
            
            if (User.IsInRole("SuperAdmin") || User.IsInRole("HR"))
            {
                // Full Access
            }
            else if (User.IsInRole("BranchAdmin") && currentUser?.BranchId != null)
            {
                query = query.Where(d => d.BranchId == currentUser.BranchId);
            }
            else
            {
                return Challenge();
            }
            
            var devices = await query.ToListAsync();
            
            var branchesQuery = _context.Branches.AsQueryable();
            if (User.IsInRole("BranchAdmin") && currentUser?.BranchId != null)
            {
                branchesQuery = branchesQuery.Where(b => b.BranchId == currentUser.BranchId);
            }
            ViewBag.Branches = await branchesQuery.ToListAsync();
            return View(devices);
        }

        public async Task<IActionResult> ExportDevices()
        {
            var devices = await _context.FingerDevices
                .Include(d => d.Branch)
                .Select(d => new
                {
                    DeviceName   = d.DeviceName,
                    IPAddress    = d.Ipaddress,
                    Port         = d.Port ?? 4370,
                    SerialNumber = d.SerialNumber ?? "",
                    Branch       = d.Branch != null ? d.Branch.BranchName : ""
                })
                .ToListAsync();

            var ms = new MemoryStream();
            ms.SaveAs(devices);
            ms.Seek(0, SeekOrigin.Begin);
            return File(ms, "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "FingerDevices.xlsx");
        }

        [HttpPost]
        public async Task<IActionResult> ImportDevices(IFormFile excelFile)
        {
            if (excelFile == null || excelFile.Length == 0)
            {
                TempData["Error"] = "يرجى اختيار ملف Excel أولاً.";
                return RedirectToAction(nameof(Index));
            }

            int added = 0, skipped = 0;
            string firstError = null;

            using var stream = excelFile.OpenReadStream();
            var rows = stream.Query(useHeaderRow: true).ToList();

            // جلب قائمة الـ IP الموجودة مسبقاً لتجنب التكرار
            var existingIps = await _context.FingerDevices.Select(d => d.Ipaddress).ToListAsync();
            // جلب الفروع لمطابقة الاسم
            var branches = await _context.Branches.ToListAsync();

            foreach (IDictionary<string, object> row in rows)
            {
                try
                {
                    var normalized = row.ToDictionary(
                        k => k.Key.Replace(" ", "").ToLower(),
                        v => v.Value?.ToString()?.Trim() ?? "");

                    string ip = normalized.GetValueOrDefault("ipaddress") 
                             ?? normalized.GetValueOrDefault("ip") ?? "";
                    string name = normalized.GetValueOrDefault("devicename") 
                               ?? normalized.GetValueOrDefault("name") ?? "";

                    if (string.IsNullOrEmpty(ip) || string.IsNullOrEmpty(name)) { skipped++; continue; }
                    if (existingIps.Contains(ip)) { skipped++; continue; }

                    int port = 4370;
                    if (normalized.ContainsKey("port") && int.TryParse(normalized["port"], out int p)) port = p;

                    string serial = normalized.GetValueOrDefault("serialnumber") 
                                 ?? normalized.GetValueOrDefault("sn") ?? "";

                    // مطابقة الفرع بالاسم
                    int? branchId = null;
                    string branchName = normalized.GetValueOrDefault("branch") 
                                    ?? normalized.GetValueOrDefault("branchname") ?? "";
                    if (!string.IsNullOrEmpty(branchName))
                    {
                        var matched = branches.FirstOrDefault(b => 
                            b.BranchName.Trim().Equals(branchName, StringComparison.OrdinalIgnoreCase));
                        branchId = matched?.BranchId;
                    }

                    _context.FingerDevices.Add(new FingerDevice
                    {
                        DeviceName   = name,
                        Ipaddress    = ip,
                        Port         = port,
                        SerialNumber = string.IsNullOrEmpty(serial) ? null : serial,
                        BranchId     = branchId,
                        IsActive     = true
                    });
                    existingIps.Add(ip);
                    added++;
                }
                catch (Exception ex)
                {
                    if (firstError == null) firstError = ex.Message;
                    skipped++;
                }
            }

            if (added > 0) await _context.SaveChangesAsync();

            TempData["Success"] = $"تم إضافة {added} جهاز بنجاح. تم تخطي {skipped} سجل.";
            if (firstError != null) TempData["Error"] = "أول خطأ: " + firstError;
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> Ping(int id)
        {
            if (!await CheckDeviceAccess(id)) return Json(new { success = false, message = "Access Denied: You do not have permission for this device." });
            
            var device = await _context.FingerDevices.FindAsync(id);
            if (device == null || string.IsNullOrEmpty(device.Ipaddress))
            {
                return Json(new { success = false, message = "Device or IP not found" });
            }

            try
            {
                using (Ping ping = new Ping())
                {
                    PingReply reply = await ping.SendPingAsync(device.Ipaddress, 2000);
                    if (reply.Status == IPStatus.Success)
                    {
                        return Json(new { success = true, status = "Connected", roundtrip = reply.RoundtripTime + "ms" });
                    }
                    else
                    {
                        return Json(new { success = false, status = "Unreachable", message = reply.Status.ToString() });
                    }
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> CheckPort(int id)
        {
            if (!await CheckDeviceAccess(id)) return Json(new { success = false, message = "Access Denied." });

            var deviceRecord = await _context.FingerDevices.FindAsync(id);
            if (deviceRecord == null || string.IsNullOrEmpty(deviceRecord.Ipaddress))
            {
                return Json(new { success = false, message = "Device or IP not found" });
            }

            int port = deviceRecord.Port ?? 4370;
            try
            {
                CZKEM device = new CZKEM();
                if (device.Connect_Net(deviceRecord.Ipaddress, port))
                {
                    device.Disconnect();
                    return Json(new { success = true, message = "Connected to ZKTeco device successfully" });
                }
                else
                {
                    int errorCode = 0;
                    device.GetLastError(ref errorCode);
                    return Json(new { success = false, message = "Failed! Error Code: " + errorCode });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }
        [HttpPost]
        public async Task<IActionResult> SyncDeviceTime(int id)
        {
            if (!await CheckDeviceAccess(id)) return Json(new { success = false, message = "Access Denied." });

            var deviceRecord = await _context.FingerDevices.FindAsync(id);
            if (deviceRecord == null || string.IsNullOrEmpty(deviceRecord.Ipaddress))
            {
                return Json(new { success = false, message = "الجهاز غير موجود." });
            }

            int port = deviceRecord.Port ?? 4370;
            try
            {
                CZKEM device = new CZKEM();
                if (device.Connect_Net(deviceRecord.Ipaddress, port))
                {
                    int year = 0, month = 0, day = 0, hour = 0, minute = 0, second = 0;
                    string oldTimeStr = "غير معروف";
                    if (device.GetDeviceTime(1, ref year, ref month, ref day, ref hour, ref minute, ref second))
                    {
                        oldTimeStr = new DateTime(year, month, day, hour, minute, second).ToString("yyyy/MM/dd hh:mm:ss tt");
                    }

                    bool setSuccess = device.SetDeviceTime(1);
                    string newTimeStr = "";
                    if (setSuccess && device.GetDeviceTime(1, ref year, ref month, ref day, ref hour, ref minute, ref second))
                    {
                        newTimeStr = new DateTime(year, month, day, hour, minute, second).ToString("yyyy/MM/dd hh:mm:ss tt");
                    }

                    device.Disconnect();

                    if (setSuccess)
                    {
                        return Json(new { 
                            success = true, 
                            message = $"تم ضبط وقت الجهاز بنجاح!\nالاسم/الجهاز: {deviceRecord.DeviceName}\nالوقت السابق على البصمة: {oldTimeStr}\nالوقت الجديد المحدث: {newTimeStr}",
                            oldTime = oldTimeStr,
                            newTime = newTimeStr,
                            serverTime = DateTime.Now.ToString("yyyy/MM/dd hh:mm:ss tt")
                        });
                    }
                    else
                    {
                        int errorCode = 0;
                        device.GetLastError(ref errorCode);
                        return Json(new { success = false, message = "فشل ضبط وقت الجهاز. كود الخطأ: " + errorCode });
                    }
                }
                else
                {
                    return Json(new { success = false, message = "لم يتم الاتصال بالجهاز على بورت " + port });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
        }

        [HttpPost]
        public async Task<IActionResult> SyncNow(int id, DateTime? fromDate = null)
        {
            if (!await CheckDeviceAccess(id)) return Json(new { success = false, message = "Access Denied." });

            var syncLock = HRSystem.Services.DeviceSyncLock.GetLock(id);
            if (!await syncLock.WaitAsync(1500))
            {
                return Json(new { success = false, message = "الجهاز قيد المزامنة حالياً، يرجى الانتظار ثواني والمحاولة مجدداً." });
            }

            var deviceRecord = await _context.FingerDevices.FindAsync(id);
            if (deviceRecord == null || string.IsNullOrEmpty(deviceRecord.Ipaddress))
            {
                syncLock.Release();
                return Json(new { success = false, message = "Device or IP not found" });
            }

            int port = deviceRecord.Port ?? 4370;
            CZKEM? device = null;
            try
            {
                device = new CZKEM();
                if (device.Connect_Net(deviceRecord.Ipaddress, port))
                {
                    device.EnableDevice(1, false);
                    if (device.ReadGeneralLogData(1))
                    {
                        string enrollNumber = "";
                        int verifyMode = 0, inOutMode = 0, year = 0, month = 0, day = 0, hour = 0, minute = 0, second = 0, workCode = 0;
                        int newLogsCount = 0;

                        int devYear = 0, devMonth = 0, devDay = 0, devHour = 0, devMinute = 0, devSecond = 0;
                        string devTimeStr = "فشل قراءة وقت الجهاز";
                        if (device.GetDeviceTime(1, ref devYear, ref devMonth, ref devDay, ref devHour, ref devMinute, ref devSecond))
                        {
                            devTimeStr = $"{devYear}/{devMonth:D2}/{devDay:D2} {devHour:D2}:{devMinute:D2}:{devSecond:D2}";
                        }

                        // 1. وقت آخر سحب لهذا الجهاز (استخدام تاريخ آمن لـ SQL Server)
                        var lastSync = deviceRecord.LastSyncTime ?? new DateTime(1900, 1, 1);
                        var fetchStart = fromDate ?? (lastSync > new DateTime(1900, 1, 1) ? lastSync.AddDays(-1) : lastSync);

                        // 2. كاش الموظفين المتاحين مع دعم مطابقة الأكواد المرنة (بدون أصفار بادئة)
                        var employees = await _context.Employees.AsNoTracking().Select(e => e.No).ToListAsync();
                        var employeeMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                        foreach (var empNo in employees)
                        {
                            var trimmed = empNo.Trim();
                            if (!employeeMap.ContainsKey(trimmed))
                                employeeMap[trimmed] = empNo;
                            
                            var normalized = trimmed.TrimStart('0');
                            if (!string.IsNullOrEmpty(normalized) && !employeeMap.ContainsKey(normalized))
                                employeeMap[normalized] = empNo;
                        }
                        
                        // 3. تحضير الـ Keys الموجودة فعلياً لهذا الجهاز (لتسريع الفحص) من بعد تاريخ بدء السحب الآمن
                        var existingKeys = await _context.Attendances
                            .Where(a => a.DeviceId == id && a.CheckTime >= fetchStart)
                            .Select(a => a.EmployeeCode + "|" + a.CheckTime.Ticks)
                            .ToListAsync();
                        var existingSet = new HashSet<string>(existingKeys);

                        int totalOnDevice = 0;
                        int skippedMissingEmployee = 0;
                        int skippedDuplicate = 0;

                        while (device.SSR_GetGeneralLogData(1, out enrollNumber, out verifyMode, out inOutMode,
                               out year, out month, out day, out hour, out minute, out second, ref workCode))
                        {
                            totalOnDevice++;
                            enrollNumber = enrollNumber.Trim();
                            if (string.IsNullOrEmpty(enrollNumber)) continue;

                            DateTime logDate;
                            try { logDate = new DateTime(year, month, day, hour, minute, second); } catch { continue; }
                            
                            // 4. تخطي كل ما هو قديم فوراً
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

                                string safeCode = enrollNumber.Length > 20 ? enrollNumber.Substring(0, 20) : enrollNumber;
                                string safeName = empName.Length > 100 ? empName.Substring(0, 100) : empName;

                                if (!await _context.Employees.AnyAsync(e => e.No == safeCode))
                                {
                                    var newEmp = new Employee
                                    {
                                        No = safeCode,
                                        SearchName = safeName,
                                        BranchId = deviceRecord.BranchId,
                                        Disabled = false,
                                        UpdatedAt = DateTime.Now
                                    };

                                    _context.Employees.Add(newEmp);
                                }

                                employeeMap[enrollNumber] = safeCode;
                                exactEmployeeCode = safeCode;
                                skippedMissingEmployee++;
                            }

                            // 5. فحص التكرار من الذاكرة
                            var key = exactEmployeeCode + "|" + logDate.Ticks;
                            if (existingSet.Contains(key)) 
                            {
                                skippedDuplicate++;
                                continue;
                            }

                            _context.Attendances.Add(new Attendance
                            {
                                EmployeeCode = exactEmployeeCode,
                                CheckTime = logDate,
                                CheckType = inOutMode == 0 ? "In" : "Out",
                                DeviceId = id,
                                VerifyMode = verifyMode
                            });
                            existingSet.Add(key);
                            newLogsCount++;
                        }

                        if (newLogsCount > 0)
                        {
                            await _context.SaveChangesAsync();
                        }

                        // دائماً نحدث وقت آخر مزامنة بمجرد نجاح الاتصال والقراءة
                        deviceRecord.LastSyncTime = DateTime.Now;
                        await _context.SaveChangesAsync();
                        
                        device.EnableDevice(1, true);
                        
                        string msg = $"تم سحب {newLogsCount} حركة جديدة من أصل {totalOnDevice} حركة على الجهاز (تاريخ المزامنة: {fetchStart:yyyy/MM/dd HH:mm}).";
                        msg += $" (وقت الجهاز حالياً: {devTimeStr})";
                        if (skippedMissingEmployee > 0) msg += $" - ({skippedMissingEmployee} حركة لموظفين غير مسجلين)";
                        if (skippedDuplicate > 0) msg += $" - ({skippedDuplicate} حركة مكررة تم تخطيها)";
                        
                        return Json(new { 
                            success = true, 
                            logs = newLogsCount, 
                            totalOnDevice = totalOnDevice,
                            skippedMissing = skippedMissingEmployee,
                            skippedDuplicate = skippedDuplicate,
                            message = msg 
                        });
                    }
                    else
                    {
                        int errorCode = 0;
                        device.GetLastError(ref errorCode);
                        device.EnableDevice(1, true);
                        deviceRecord.LastSyncTime = DateTime.Now;
                        await _context.SaveChangesAsync();
                        return Json(new { success = false, message = $"الفشل في قراءة البيانات (تأكد من رقم Machine ID = 1) - كود الخطأ: {errorCode}" });
                    }
                }
                else
                {
                    int errorCode = 0;
                    device.GetLastError(ref errorCode);
                    return Json(new { success = false, message = $"لم يتم الاتصال بالجهاز على بورت {port} (كود الخطأ: {errorCode})" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "Error: " + ex.Message });
            }
            finally
            {
                if (device != null)
                {
                    try { device.Disconnect(); } catch { }
                    try { System.Runtime.InteropServices.Marshal.ReleaseComObject(device); } catch { }
                }
                syncLock.Release();
            }
        }
        [HttpGet]
        public async Task<IActionResult> GetDeviceZkInfo(int id)
        {
            var device = await _context.FingerDevices.Include(d => d.Branch).FirstOrDefaultAsync(d => d.DeviceId == id);
            if (device == null) return Json(new { success = false, message = "الجهاز غير موجود" });

            string path = !string.IsNullOrWhiteSpace(device.AccessDbPath) 
                ? device.AccessDbPath 
                : (device.Branch?.ZkAccessDbPath ?? "");

            return Json(new { 
                success = true, 
                deviceId = device.DeviceId,
                deviceName = device.DeviceName,
                branchName = device.Branch?.BranchName ?? "بدون فرع",
                accessDbPath = path
            });
        }

        [HttpPost]
        public async Task<IActionResult> TestZkAccessDb(string path)
        {
            var branch = await _context.Branches.FirstOrDefaultAsync(b => (b.ZkAccessDbPath != null && b.ZkAccessDbPath == path) || (path != null && b.VpnIp != null && path.Contains(b.VpnIp)));
            var (success, message) = await _zkAccessService.TestConnectionAsync(path, branch?.ZkUsername, branch?.ZkPassword);
            return Json(new { success, message });
        }

        [HttpPost]
        public async Task<IActionResult> AddUserToDevice(
            int deviceId, 
            string userCode, 
            string userName, 
            bool sendToDevice = true, 
            bool sendToAccessDb = false, 
            string? customAccessPath = null)
        {
            if (!await CheckDeviceAccess(deviceId)) return Json(new { success = false, message = "Access Denied." });

            if (string.IsNullOrEmpty(userCode) || string.IsNullOrEmpty(userName))
            {
                return Json(new { success = false, message = "كود المستخدم واسمه مطلوبان." });
            }

            var deviceRecord = await _context.FingerDevices.Include(d => d.Branch).FirstOrDefaultAsync(d => d.DeviceId == deviceId);
            if (deviceRecord == null || string.IsNullOrEmpty(deviceRecord.Ipaddress))
            {
                return Json(new { success = false, message = "الجهاز غير موجود." });
            }

            if (!sendToDevice && !sendToAccessDb)
            {
                return Json(new { success = false, message = "يرجى تحديد وجهة واحدة على الأقل (ماكينة البصمة أو داتابيز ZK)." });
            }

            var resultMessages = new List<string>();
            bool overallSuccess = false;

            // 1. إرسال إلى ماكينة البصمة مباشرة عبر الـ SDK
            if (sendToDevice)
            {
                int port = deviceRecord.Port ?? 4370;
                try
                {
                    CZKEM device = new CZKEM();
                    if (device.Connect_Net(deviceRecord.Ipaddress, port))
                    {
                        string encodedName = EncodeForZKTeco(userName);
                        if (device.SSR_SetUserInfo(1, userCode, encodedName, "", 0, true))
                        {
                            device.RefreshData(1);
                            device.Disconnect();
                            resultMessages.Add($"✔️ ماكينة البصمة ({deviceRecord.DeviceName}): تم إرسال الموظف بنجاح.");
                            overallSuccess = true;
                        }
                        else
                        {
                            int errorCode = 0;
                            device.GetLastError(ref errorCode);
                            device.Disconnect();
                            resultMessages.Add($"❌ ماكينة البصمة ({deviceRecord.DeviceName}): فشل، كود الخطأ {errorCode}.");
                        }
                    }
                    else
                    {
                        resultMessages.Add($"❌ ماكينة البصمة ({deviceRecord.DeviceName}): تعذر الاتصال على بورت {port}.");
                    }
                }
                catch (Exception ex)
                {
                    resultMessages.Add($"❌ ماكينة البصمة ({deviceRecord.DeviceName}): {ex.Message}");
                }
            }

            // 2. إضافة إلى قاعدة بيانات برنامج ZKTeco (Access DB - att2000.mdb)
            if (sendToAccessDb)
            {
                string? targetMdbPath = !string.IsNullOrWhiteSpace(customAccessPath)
                    ? customAccessPath
                    : (!string.IsNullOrWhiteSpace(deviceRecord.AccessDbPath) 
                        ? deviceRecord.AccessDbPath 
                        : deviceRecord.Branch?.ZkAccessDbPath);

                if (string.IsNullOrWhiteSpace(targetMdbPath))
                {
                    resultMessages.Add("⚠️ داتابيز ZKTeco (Access): لم يتم تحديد مسار قاعدة بيانات الفرع (att2000.mdb).");
                }
                else
                {
                    var (accSuccess, accMsg) = await _zkAccessService.AddOrUpdateUserInAccessDbAsync(
                        targetMdbPath, 
                        userCode, 
                        userName, 
                        deviceRecord.Ipaddress,
                        deviceRecord.Branch?.ZkUsername,
                        deviceRecord.Branch?.ZkPassword);

                    if (accSuccess)
                    {
                        resultMessages.Add($"✔️ داتابيز ZKTeco (Access): تم حفظ الموظف بنجاح.");
                        overallSuccess = true;
                    }
                    else
                    {
                        resultMessages.Add($"❌ داتابيز ZKTeco (Access): {accMsg}");
                    }
                }
            }

            return Json(new { 
                success = overallSuccess, 
                message = string.Join("\n", resultMessages) 
            });
        }

        private static bool IsTcpPortOpen(string host, int port, int timeoutMs = 1200)
        {
            try
            {
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    var result = client.BeginConnect(host, port, null, null);
                    var success = result.AsyncWaitHandle.WaitOne(timeoutMs);
                    if (!success) return false;
                    client.EndConnect(result);
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        [HttpGet]
        public IActionResult DownloadEmployeeImportTemplate()
        {
            try
            {
                using var workbook = new XLWorkbook();
                var worksheet = workbook.Worksheets.Add("Employees");
                worksheet.RightToLeft = true;

                // Headers
                worksheet.Cell(1, 1).Value = "كود الموظف";
                worksheet.Cell(1, 2).Value = "اسم الموظف";

                var headerRow = worksheet.Row(1);
                headerRow.Height = 25;
                headerRow.Style.Font.Bold = true;
                headerRow.Style.Font.FontSize = 11;
                headerRow.Style.Font.FontColor = XLColor.White;
                headerRow.Style.Fill.BackgroundColor = XLColor.FromHtml("#4e73df");
                headerRow.Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                headerRow.Style.Alignment.Vertical = XLAlignmentVerticalValues.Center;

                // Sample data
                worksheet.Cell(2, 1).Value = "1001";
                worksheet.Cell(2, 2).Value = "أحمد محمد علي";
                worksheet.Cell(3, 1).Value = "1002";
                worksheet.Cell(3, 2).Value = "محمود إبراهيم حسن";
                worksheet.Cell(4, 1).Value = "1003";
                worksheet.Cell(4, 2).Value = "سارة عبد الله أحمد";

                worksheet.Column(1).Style.NumberFormat.Format = "@";
                worksheet.Column(1).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Center;
                worksheet.Column(2).Style.Alignment.Horizontal = XLAlignmentHorizontalValues.Right;

                worksheet.Columns().AdjustToContents();
                worksheet.Column(1).Width = 20;
                worksheet.Column(2).Width = 35;

                using var stream = new MemoryStream();
                workbook.SaveAs(stream);
                stream.Position = 0;

                return File(stream.ToArray(), "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", "Employees_Biometric_Template.xlsx");
            }
            catch (Exception ex)
            {
                return BadRequest("فشل إنشاء القالب: " + ex.Message);
            }
        }

        [HttpPost]
        public async Task<IActionResult> ImportUsersToDevicesFromExcel(IFormFile excelFile, int? deviceId, int? branchId, bool syncWithDb = true)
        {
            if (excelFile == null || excelFile.Length == 0)
            {
                return Json(new { success = false, message = "يرجى اختيار ملف Excel صالح." });
            }

            try
            {
                var tempPath = Path.GetTempFileName();
                using (var fileStream = System.IO.File.Create(tempPath))
                {
                    await excelFile.CopyToAsync(fileStream);
                }

                var employeeList = new List<(string Code, string Name)>();

                // 1. Read Excel using MiniExcel
                using (var stream = System.IO.File.OpenRead(tempPath))
                {
                    var rows = MiniExcel.Query(stream, useHeaderRow: true).ToList();
                    foreach (IDictionary<string, object> row in rows)
                    {
                        var normalizedRow = row.ToDictionary(
                            k => (k.Key ?? "").Replace(" ", "").Replace(".", "").Replace("_", "").Trim().ToLower(),
                            v => v.Value?.ToString()?.Trim() ?? "");

                        string empNo = normalizedRow.GetValueOrDefault("code") 
                                    ?? normalizedRow.GetValueOrDefault("usercode") 
                                    ?? normalizedRow.GetValueOrDefault("employeecode")
                                    ?? normalizedRow.GetValueOrDefault("no")
                                    ?? normalizedRow.GetValueOrDefault("empno")
                                    ?? normalizedRow.GetValueOrDefault("pin")
                                    ?? normalizedRow.GetValueOrDefault("كود")
                                    ?? normalizedRow.GetValueOrDefault("كودالموظف")
                                    ?? normalizedRow.GetValueOrDefault("الرقم")
                                    ?? normalizedRow.GetValueOrDefault("رقمالموظف")
                                    ?? "";

                        string empName = normalizedRow.GetValueOrDefault("name") 
                                      ?? normalizedRow.GetValueOrDefault("username")
                                      ?? normalizedRow.GetValueOrDefault("employeename")
                                      ?? normalizedRow.GetValueOrDefault("searchname")
                                      ?? normalizedRow.GetValueOrDefault("fullname")
                                      ?? normalizedRow.GetValueOrDefault("الاسم")
                                      ?? normalizedRow.GetValueOrDefault("اسمالموظف")
                                      ?? normalizedRow.GetValueOrDefault("الاسمبالكامل")
                                      ?? "";

                        // Clean numbers
                        if (!string.IsNullOrWhiteSpace(empNo) && double.TryParse(empNo, out var parsedNum) && !empNo.StartsWith("0"))
                        {
                            empNo = ((long)parsedNum).ToString();
                        }

                        if (!string.IsNullOrWhiteSpace(empNo) && !string.IsNullOrWhiteSpace(empName))
                        {
                            if (!employeeList.Any(e => e.Code == empNo))
                            {
                                employeeList.Add((empNo.Trim(), empName.Trim()));
                            }
                        }
                    }
                }

                if (System.IO.File.Exists(tempPath))
                {
                    System.IO.File.Delete(tempPath);
                }

                if (!employeeList.Any())
                {
                    return Json(new { success = false, message = "لم يتم العثور على أعمدة صالحة (كود الموظف / اسم الموظف) أو بيانات داخل ملف Excel." });
                }

                // 2. Database Sync
                int dbAdded = 0, dbUpdated = 0;
                if (syncWithDb)
                {
                    var existingEmployees = await _context.Employees.ToListAsync();
                    var existingDict = existingEmployees.ToDictionary(e => e.No, StringComparer.OrdinalIgnoreCase);

                    foreach (var emp in employeeList)
                    {
                        if (existingDict.TryGetValue(emp.Code, out var existingEmp))
                        {
                            existingEmp.SearchName = emp.Name;
                            existingEmp.UpdatedAt = DateTime.Now;
                            if (branchId.HasValue && branchId.Value > 0 && (existingEmp.BranchId == null || existingEmp.BranchId == 0))
                            {
                                existingEmp.BranchId = branchId.Value;
                            }
                            dbUpdated++;
                        }
                        else
                        {
                            var newEmp = new Employee
                            {
                                No = emp.Code,
                                SearchName = emp.Name,
                                BranchId = (branchId.HasValue && branchId.Value > 0) ? branchId.Value : null,
                                EmployeeStatus = "Active",
                                UpdatedAt = DateTime.Now
                            };
                            _context.Employees.Add(newEmp);
                            existingDict[emp.Code] = newEmp;
                            dbAdded++;
                        }
                    }
                    await _context.SaveChangesAsync();
                }

                // 3. Resolve Target Devices
                var currentUser = await _userManager.GetUserAsync(User);
                var devicesQuery = _context.FingerDevices.Include(d => d.Branch).Where(d => d.IsActive == true);
                
                if (User.IsInRole("BranchAdmin") && currentUser?.BranchId != null)
                {
                    devicesQuery = devicesQuery.Where(d => d.BranchId == currentUser.BranchId);
                }

                if (deviceId.HasValue && deviceId.Value > 0)
                {
                    devicesQuery = devicesQuery.Where(d => d.DeviceId == deviceId.Value);
                }
                else if (branchId.HasValue && branchId.Value > 0)
                {
                    devicesQuery = devicesQuery.Where(d => d.BranchId == branchId.Value);
                }

                var devices = await devicesQuery.ToListAsync();
                if (!devices.Any())
                {
                    return Json(new { 
                        success = true, 
                        message = $"تم حفظ {employeeList.Count} موظف في قاعدة البيانات بنجاح ({dbAdded} جديد، {dbUpdated} تحديث)، ولكن لم يتم العثور على أجهزة بصمة نشطة لنقل الأسماء إليها.",
                        totalEmployees = employeeList.Count,
                        syncedDevices = 0,
                        totalTargetDevices = 0,
                        logs = new List<string> { "لم يتم العثور على أجهزة نشطة مطابقة للاختيار" }
                    });
                }

                int syncedDevicesCount = 0;
                var logs = new List<string>();
                var deviceDetails = new List<object>();

                foreach (var dev in devices)
                {
                    int port = dev.Port ?? 4370;
                    if (!IsTcpPortOpen(dev.Ipaddress, port, 1200))
                    {
                        logs.Add($"❌ الجهاز غير متاح على الشبكة: {dev.DeviceName} ({dev.Ipaddress}:{port})");
                        deviceDetails.Add(new { deviceName = dev.DeviceName, ip = dev.Ipaddress, status = "غير متاح", count = 0 });
                        continue;
                    }

                    try
                    {
                        CZKEM zk = new CZKEM();
                        if (zk.Connect_Net(dev.Ipaddress, port))
                        {
                            zk.EnableDevice(1, false);
                            int successOnDevice = 0;

                            foreach (var emp in employeeList)
                            {
                                string encName = HRSystem.Services.ZKTecoHelper.EncodeForZKTeco(emp.Name);
                                if (zk.SSR_SetUserInfo(1, emp.Code, encName, "", 0, true))
                                {
                                    successOnDevice++;
                                }
                            }

                            zk.RefreshData(1);
                            zk.EnableDevice(1, true);
                            zk.Disconnect();

                            dev.LastSyncTime = DateTime.Now;
                            await _context.SaveChangesAsync();

                            syncedDevicesCount++;
                            logs.Add($"✅ تم إرسال {successOnDevice} موظف إلى: {dev.DeviceName}");
                            deviceDetails.Add(new { deviceName = dev.DeviceName, ip = dev.Ipaddress, status = "تم بنجاح", count = successOnDevice });
                        }
                        else
                        {
                            logs.Add($"❌ تعذر فتح جلسة اتصال مع: {dev.DeviceName}");
                            deviceDetails.Add(new { deviceName = dev.DeviceName, ip = dev.Ipaddress, status = "فشل الاتصال", count = 0 });
                        }
                    }
                    catch (Exception ex)
                    {
                        logs.Add($"⚠️ خطأ في جهاز {dev.DeviceName}: {ex.Message}");
                        deviceDetails.Add(new { deviceName = dev.DeviceName, ip = dev.Ipaddress, status = "خطأ: " + ex.Message, count = 0 });
                    }
                }

                string finalMsg = $"تمت معالجة {employeeList.Count} موظف بنجاح وإرسالهم إلى {syncedDevicesCount} من أصل {devices.Count} أجهزة بصمة.";
                if (syncWithDb)
                {
                    finalMsg += $" (قاعدة البيانات: {dbAdded} موظف جديد، {dbUpdated} تم تحديثهم).";
                }

                return Json(new {
                    success = true,
                    message = finalMsg,
                    totalEmployees = employeeList.Count,
                    syncedDevices = syncedDevicesCount,
                    totalTargetDevices = devices.Count,
                    deviceDetails = deviceDetails,
                    logs = logs
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "حدث خطأ أثناء استيراد البيانات: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetDeviceTime(int id)
        {
            if (!await CheckDeviceAccess(id)) return Json(new { success = false, message = "Access Denied." });

            var deviceRecord = await _context.FingerDevices.FindAsync(id);
            if (deviceRecord == null || string.IsNullOrEmpty(deviceRecord.Ipaddress))
            {
                return Json(new { success = false, message = "الجهاز غير موجود." });
            }

            int port = deviceRecord.Port ?? 4370;
            try
            {
                CZKEM device = new CZKEM();
                if (device.Connect_Net(deviceRecord.Ipaddress, port))
                {
                    int year = 0, month = 0, day = 0, hour = 0, minute = 0, second = 0;
                    if (device.GetDeviceTime(1, ref year, ref month, ref day, ref hour, ref minute, ref second))
                    {
                        DateTime deviceTime = new DateTime(year, month, day, hour, minute, second);
                        DateTime serverTime = DateTime.Now;
                        TimeSpan diff = deviceTime - serverTime;

                        device.Disconnect();

                        return Json(new {
                            success = true,
                            deviceName = deviceRecord.DeviceName,
                            deviceTime = deviceTime.ToString("yyyy/MM/dd hh:mm:ss tt"),
                            serverTime = serverTime.ToString("yyyy/MM/dd hh:mm:ss tt"),
                            diffMinutes = Math.Round(diff.TotalMinutes, 1),
                            isAccurate = Math.Abs(diff.TotalMinutes) < 1,
                            message = Math.Abs(diff.TotalMinutes) < 1 
                                ? "وقت جهاز البصمة مضبوط ودقيق بنفس وقت الخادم." 
                                : $"يوجد فرق وقت قدره {Math.Abs(Math.Round(diff.TotalMinutes, 1))} دقيقة بين جهاز البصمة والخادم."
                        });
                    }
                    else
                    {
                        device.Disconnect();
                        return Json(new { success = false, message = "فشل في قراءة الوقت الحالي من جهاز البصمة." });
                    }
                }
                else
                {
                    return Json(new { success = false, message = "تعذر الاتصال بجهاز البصمة على بورت " + port });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "خطأ في الاتصال: " + ex.Message });
            }
        }



        [HttpGet]
        public async Task<IActionResult> CreateDevice()
        {
            ViewBag.Branches = await _context.Branches.ToListAsync();
            return View();
        }

        [HttpPost]
        public async Task<IActionResult> CreateDevice(FingerDevice device)
        {
            if (ModelState.IsValid)
            {
                device.IsActive = true;
                _context.FingerDevices.Add(device);
                await _context.SaveChangesAsync();
                TempData["Success"] = "تم إضافة الجهاز بنجاح.";
                return RedirectToAction(nameof(FingerDevices));
            }
            ViewBag.Branches = await _context.Branches.ToListAsync();
            return View(device);
        }

        [HttpGet]
        public async Task<IActionResult> EditDevice(int id)
        {
            if (!await CheckDeviceAccess(id)) return Forbid();

            var device = await _context.FingerDevices.FindAsync(id);
            if (device == null) return NotFound();
            ViewBag.Branches = await _context.Branches.ToListAsync();
            return View(device);
        }

        [HttpPost]
        public async Task<IActionResult> EditDevice(FingerDevice device)
        {
            if (!await CheckDeviceAccess(device.DeviceId)) return Forbid();

            if (ModelState.IsValid)
            {
                _context.Update(device);
                await _context.SaveChangesAsync();
                TempData["Success"] = "تم تحديث بيانات الجهاز.";
                return RedirectToAction(nameof(FingerDevices));
            }
            ViewBag.Branches = await _context.Branches.ToListAsync();
            return View(device);
        }

        [HttpPost]
        public async Task<IActionResult> DeleteDevice(int id)
        {
            if (!await CheckDeviceAccess(id)) return Json(new { success = false, message = "Access Denied." });

            var device = await _context.FingerDevices.FindAsync(id);
            if (device != null)
            {
                _context.FingerDevices.Remove(device);
                await _context.SaveChangesAsync();
                return Json(new { success = true, message = "تم حذف الجهاز بنجاح." });
            }
            return Json(new { success = false, message = "الجهاز غير موجود." });
        }

        [HttpPost]
        public async Task<IActionResult> CreateBranch(Branch branch)
        {
            if (ModelState.IsValid)
            {
                var codeExists = await _context.Branches.AnyAsync(b => b.BranchCode == branch.BranchCode);
                if (codeExists)
                {
                    TempData["Error"] = "كود الفرع مستخدم بالفعل لفرع آخر.";
                    return RedirectToAction(nameof(Index));
                }

                branch.IsActive = true;
                _context.Branches.Add(branch);
                await _context.SaveChangesAsync();
                TempData["Success"] = "تم إضافة الفرع بنجاح.";
                return RedirectToAction(nameof(Index));
            }
            TempData["Error"] = "البيانات المدخلة غير صالحة.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> EditBranch(Branch branch)
        {
            if (ModelState.IsValid)
            {
                var existingBranch = await _context.Branches.FindAsync(branch.BranchId);
                if (existingBranch == null) return NotFound();

                var codeExists = await _context.Branches.AnyAsync(b => b.BranchCode == branch.BranchCode && b.BranchId != branch.BranchId);
                if (codeExists)
                {
                    TempData["Error"] = "كود الفرع مستخدم بالفعل لفرع آخر.";
                    return RedirectToAction(nameof(Index));
                }

                existingBranch.BranchName = branch.BranchName;
                existingBranch.BranchCode = branch.BranchCode;
                existingBranch.City = branch.City;
                existingBranch.VpnIp = branch.VpnIp;
                existingBranch.IsActive = branch.IsActive;
                existingBranch.ZkAccessDbPath = branch.ZkAccessDbPath;

                _context.Update(existingBranch);
                await _context.SaveChangesAsync();
                TempData["Success"] = "تم تحديث بيانات الفرع بنجاح.";
                return RedirectToAction(nameof(Index));
            }
            TempData["Error"] = "البيانات المدخلة غير صالحة.";
            return RedirectToAction(nameof(Index));
        }

        [HttpPost]
        public async Task<IActionResult> DeleteBranch(int id)
        {
            var branch = await _context.Branches
                .Include(b => b.Employees)
                .Include(b => b.FingerDevices)
                .Include(b => b.DvrDevices)
                .FirstOrDefaultAsync(b => b.BranchId == id);

            if (branch == null)
            {
                return Json(new { success = false, message = "الفرع غير موجود." });
            }

            if (branch.Employees.Any() || branch.FingerDevices.Any() || branch.DvrDevices.Any())
            {
                return Json(new { 
                    success = false, 
                    message = "لا يمكن حذف الفرع لوجود موظفين أو أجهزة بصمة أو أجهزة DVR مرتبطة به." 
                });
            }

            try
            {
                _context.Branches.Remove(branch);
                await _context.SaveChangesAsync();
                return Json(new { success = true, message = "تم حذف الفرع بنجاح." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"فشل حذف الفرع: {ex.Message}" });
            }
        }
    }
}
