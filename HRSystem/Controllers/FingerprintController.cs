using HRSystem.Models;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using zkemkeeper;

namespace HRSystem.Controllers
{
    [Authorize]
    public class FingerprintController : Controller
    {
        private readonly HRContext _context;
        private readonly ILogger<FingerprintController> _logger;

        public FingerprintController(HRContext context, ILogger<FingerprintController> logger)
        {
            _context = context;
            _logger = logger;
        }

        [HttpGet]
        public async Task<IActionResult> GetDevices()
        {
            var devices = await _context.FingerDevices
                .Where(d => d.IsActive == true)
                .Select(d => new { d.DeviceId, d.DeviceName, d.Ipaddress })
                .ToListAsync();
            return Json(devices);
        }

        [HttpPost]
        public async Task<IActionResult> Transfer(string employeeNo, int sourceDeviceId, int[] targetDeviceIds)
        {
            if (string.IsNullOrEmpty(employeeNo) || sourceDeviceId == 0 || targetDeviceIds == null || targetDeviceIds.Length == 0)
            {
                return Json(new { success = false, message = "بيانات غير مكتملة." });
            }

            var sourceDeviceRecord = await _context.FingerDevices.FindAsync(sourceDeviceId);
            if (sourceDeviceRecord == null) return Json(new { success = false, message = "جهاز المصدر غير موجود." });

            CZKEM sourceDevice = new CZKEM();
            if (!sourceDevice.Connect_Net(sourceDeviceRecord.Ipaddress, sourceDeviceRecord.Port ?? 4370))
            {
                return Json(new { success = false, message = $"فشل الاتصال بجهاز المصدر: {sourceDeviceRecord.DeviceName}" });
            }

            try
            {
                string name = "", password = "";
                int privilege = 0;
                bool enabled = false;

                // 1. الحصول على بيانات الموظف من جهاز المصدر
                if (!sourceDevice.SSR_GetUserInfo(1, employeeNo, out name, out password, out privilege, out enabled))
                {
                    sourceDevice.Disconnect();
                    return Json(new { success = false, message = "الموظف غير مسجل على جهاز المصدر." });
                }

                // 2. الحصول على القوالب (Fingerprint Templates)
                // الأجهزة تدعم حتى 10 أصابع (0-9)
                var templates = new List<(int index, int flag, string data, int length)>();
                for (int i = 0; i < 10; i++)
                {
                    int flag = 0;
                    string tmpData = "";
                    int tmpLength = 0;
                    if (sourceDevice.GetUserTmpExStr(1, employeeNo, i, out flag, out tmpData, out tmpLength))
                    {
                        templates.Add((i, flag, tmpData, tmpLength));
                    }
                }

                sourceDevice.Disconnect();

                if (templates.Count == 0)
                {
                    return Json(new { success = false, message = "لم يتم العثور على بصمات للموظف في جهاز المصدر." });
                }

                // 3. النقل للأجهزة الهدف بالتوازي لتسريع العملية
                int successCount = 0;
                List<string> errors = new List<string>();

                var transferTasks = targetDeviceIds.Select(async targetId =>
                {
                    var targetDeviceRecord = await _context.FingerDevices.FindAsync(targetId);
                    if (targetDeviceRecord == null) return;

                    try
                    {
                        CZKEM targetDevice = new CZKEM();
                        // نستخدم مهلة زمنية قصيرة للاتصال لتجنب الانتظار الطويل للأجهزة المغلقة
                        if (targetDevice.Connect_Net(targetDeviceRecord.Ipaddress, targetDeviceRecord.Port ?? 4370))
                        {
                            if (targetDevice.SSR_SetUserInfo(1, employeeNo, name, password, privilege, enabled))
                            {
                                bool allTemplatesSet = true;
                                foreach (var temp in templates)
                                {
                                    if (!targetDevice.SetUserTmpExStr(1, employeeNo, temp.index, temp.flag, temp.data))
                                    {
                                        allTemplatesSet = false;
                                    }
                                }

                                if (allTemplatesSet)
                                {
                                    targetDevice.RefreshData(1);
                                    Interlocked.Increment(ref successCount);
                                }
                                else
                                {
                                    lock (errors) { errors.Add($"فشل نقل بعض البصمات إلى: {targetDeviceRecord.DeviceName}"); }
                                }
                            }
                            else
                            {
                                lock (errors) { errors.Add($"فشل تعيين بيانات الموظف في: {targetDeviceRecord.DeviceName}"); }
                            }
                            targetDevice.Disconnect();
                        }
                        else
                        {
                            lock (errors) { errors.Add($"فشل الاتصال بجهاز الهدف: {targetDeviceRecord.DeviceName}"); }
                        }
                    }
                    catch (Exception ex)
                    {
                        lock (errors) { errors.Add($"خطأ تقني في جهاز {targetDeviceRecord.DeviceName}: {ex.Message}"); }
                    }
                });

                await Task.WhenAll(transferTasks);

                return Json(new { 
                    success = successCount > 0, 
                    message = successCount > 0 ? $"تم النقل بنجاح إلى {successCount} أجهزة." : "فشل النقل إلى جميع الأجهزة المحددة.",
                    errors = errors 
                });
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error during fingerprint transfer");
                return Json(new { success = false, message = "حدث خطأ غير متوقع: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> GetDeviceUserInfo(string employeeNo, int? deviceId, int? branchId)
        {
            if (string.IsNullOrEmpty(employeeNo))
            {
                return Json(new { success = false, message = "يرجى تقديم كود الموظف." });
            }

            var devicesQuery = _context.FingerDevices.Where(d => d.IsActive == true);
            if (deviceId.HasValue && deviceId.Value > 0)
            {
                devicesQuery = devicesQuery.Where(d => d.DeviceId == deviceId.Value);
            }
            else if (branchId.HasValue && branchId.Value > 0)
            {
                devicesQuery = devicesQuery.Where(d => d.BranchId == branchId.Value);
            }

            var device = await devicesQuery.FirstOrDefaultAsync();
            if (device == null)
            {
                return Json(new { success = false, message = "لم يتم العثور على جهاز بصمة نشط ومتصل." });
            }

            try
            {
                CZKEM zk = new CZKEM();
                if (zk.Connect_Net(device.Ipaddress, device.Port ?? 4370))
                {
                    string rawName = "", password = "";
                    int privilege = 0;
                    bool enabled = false;

                    bool found = zk.SSR_GetUserInfo(1, employeeNo, out rawName, out password, out privilege, out enabled);
                    zk.Disconnect();

                    if (found)
                    {
                        string decodedName = HRSystem.Services.ZKTecoHelper.DecodeFromZKTeco(rawName);
                        return Json(new {
                            success = true,
                            employeeNo = employeeNo,
                            deviceName = device.DeviceName,
                            ipAddress = device.Ipaddress,
                            rawNameOnDevice = rawName,
                            oldNameOnDevice = decodedName,
                            privilege = privilege,
                            enabled = enabled
                        });
                    }
                    else
                    {
                        return Json(new { success = false, message = $"الموظف (كود {employeeNo}) غير موجود حالياً على جهاز البصمة ({device.DeviceName})." });
                    }
                }
                else
                {
                    return Json(new { success = false, message = $"تعذر الاتصال بجهاز البصمة: {device.DeviceName} ({device.Ipaddress})" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "خطأ في الاتصال بالجهاز: " + ex.Message });
            }
        }

        private static bool IsTcpPortOpen(string host, int port, int timeoutMs = 1200)
        {
            try
            {
                using (var client = new System.Net.Sockets.TcpClient())
                {
                    var result = client.BeginConnect(host, port, null, null);
                    var success = result.AsyncWaitHandle.WaitOne(TimeSpan.FromMilliseconds(timeoutMs));
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

        [HttpPost]
        public async Task<IActionResult> UpdateDeviceName(string employeeNo, string newName, int? branchId, int? deviceId)
        {
            if (string.IsNullOrEmpty(employeeNo) || string.IsNullOrEmpty(newName))
            {
                return Json(new { success = false, message = "برجاء إدخال كود الموظف والاسم الجديد." });
            }

            // 1. Update in Database if employee exists
            var emp = await _context.Employees.FirstOrDefaultAsync(e => e.No == employeeNo);
            string dbOldName = emp?.SearchName;
            if (emp != null)
            {
                emp.SearchName = newName;
                emp.UpdatedAt = DateTime.Now;
                await _context.SaveChangesAsync();
            }

            // 2. Determine target devices
            var devicesQuery = _context.FingerDevices.Where(d => d.IsActive == true);
            if (deviceId.HasValue && deviceId.Value > 0)
            {
                devicesQuery = devicesQuery.Where(d => d.DeviceId == deviceId.Value);
            }
            else if (branchId.HasValue && branchId.Value > 0)
            {
                devicesQuery = devicesQuery.Where(d => d.BranchId == branchId.Value);
            }
            else if (emp?.BranchId != null)
            {
                devicesQuery = devicesQuery.Where(d => d.BranchId == emp.BranchId);
            }
            else
            {
                return Json(new { success = false, message = "يرجى اختيار جهاز البصمة أو الفرع لحفظ وتسميع الاسم." });
            }

            var devices = await devicesQuery.ToListAsync();
            if (!devices.Any())
            {
                return Json(new { success = false, message = "لم يتم العثور على أجهزة بصمة نشطة متصلة إما للفرع أو الجهاز المحدد." });
            }

            string encodedName = HRSystem.Services.ZKTecoHelper.EncodeForZKTeco(newName);
            int successCount = 0;
            List<string> errors = new List<string>();
            List<object> deviceDetails = new List<object>();

            foreach (var dev in devices)
            {
                int port = dev.Port ?? 4370;
                if (!IsTcpPortOpen(dev.Ipaddress, port, 1200))
                {
                    errors.Add($"الجهاز غير متاح على الشبكة حالياً: {dev.DeviceName} ({dev.Ipaddress}:{port})");
                    continue;
                }

                try
                {
                    CZKEM zk = new CZKEM();
                    if (zk.Connect_Net(dev.Ipaddress, port))
                    {
                        string pwd = ""; int priv = 0; bool enabled = true; string rawOldName = "";
                        bool found = zk.SSR_GetUserInfo(1, employeeNo, out rawOldName, out pwd, out priv, out enabled);
                        string oldNameDecoded = found ? HRSystem.Services.ZKTecoHelper.DecodeFromZKTeco(rawOldName) : "جديد / غير مسجل";

                        if (zk.SSR_SetUserInfo(1, employeeNo, encodedName, pwd, priv, enabled))
                        {
                            zk.RefreshData(1);
                            successCount++;
                            deviceDetails.Add(new {
                                deviceName = dev.DeviceName,
                                oldNameOnDevice = oldNameDecoded,
                                newNameOnDevice = newName
                            });
                        }
                        else
                        {
                            errors.Add($"فشل كتابة الاسم على الجهاز: {dev.DeviceName} ({dev.Ipaddress})");
                        }
                        zk.Disconnect();
                    }
                    else
                    {
                        int errCode = 0;
                        zk.GetLastError(ref errCode);
                        errors.Add($"تعذر الاتصال ببطاقة الشبكة لجهاز: {dev.DeviceName} ({dev.Ipaddress}) - كود الخطأ: {errCode}");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"خطأ بالجهاز {dev.DeviceName}: {ex.Message}");
                }
            }

            bool isSuccessful = successCount > 0;
            string msg = isSuccessful 
                ? $"تم حفظ وتسميع الاسم بنجاح على شريحة جهاز البصمة ({deviceDetails.Count} جهاز)." 
                : $"تعذر التحديث على البصمة. التفاصيل: {string.Join(" | ", errors)}";

            return Json(new { 
                success = isSuccessful, 
                message = msg,
                dbOldName = dbOldName,
                deviceDetails = deviceDetails,
                errors = errors
            });
        }

        private int devDetailsCount(List<object> list) => list?.Count ?? 0;

        [HttpPost]
        public async Task<IActionResult> ImportAndSyncNames(IFormFile excelFile, int? branchId, int? deviceId)
        {
            if (excelFile == null || excelFile.Length <= 0)
            {
                return Json(new { success = false, message = "يرجى اختيار ملف Excel صالح." });
            }

            try
            {
                var tempPath = Path.GetTempFileName();
                using (var stream = System.IO.File.Create(tempPath))
                {
                    await excelFile.CopyToAsync(stream);
                }

                List<Dictionary<string, string>> updatedList = new List<Dictionary<string, string>>();
                using (var stream = System.IO.File.OpenRead(tempPath))
                {
                    var rows = MiniExcelLibs.MiniExcel.Query(stream, useHeaderRow: true).ToList();
                    foreach (IDictionary<string, object> row in rows)
                    {
                        var normalizedRow = row.ToDictionary(
                            k => k.Key.Replace(" ", "").Replace(".", "").Trim().ToLower(),
                            v => v.Value?.ToString()?.Trim());

                        string empNo = normalizedRow.GetValueOrDefault("no") 
                                    ?? normalizedRow.GetValueOrDefault("code") 
                                    ?? normalizedRow.GetValueOrDefault("employeecode");

                        string newName = normalizedRow.GetValueOrDefault("searchname") 
                                     ?? normalizedRow.GetValueOrDefault("name") 
                                     ?? normalizedRow.GetValueOrDefault("employeename");

                        if (!string.IsNullOrEmpty(empNo) && !string.IsNullOrEmpty(newName))
                        {
                            var emp = await _context.Employees.FirstOrDefaultAsync(e => e.No == empNo);
                            if (emp != null)
                            {
                                emp.SearchName = newName;
                                emp.UpdatedAt = DateTime.Now;
                            }
                            updatedList.Add(new Dictionary<string, string> { { "Code", empNo }, { "Name", newName } });
                        }
                    }
                    await _context.SaveChangesAsync();
                }

                System.IO.File.Delete(tempPath);

                if (!updatedList.Any())
                {
                    return Json(new { success = false, message = "لم يتم العثور على أعمدة صالحة (Code / Name) في ملف Excel." });
                }

                // Push all updated names to biometric devices
                var devicesQuery = _context.FingerDevices.Where(d => d.IsActive == true);
                if (deviceId.HasValue && deviceId.Value > 0)
                {
                    devicesQuery = devicesQuery.Where(d => d.DeviceId == deviceId.Value);
                }
                else if (branchId.HasValue && branchId.Value > 0)
                {
                    devicesQuery = devicesQuery.Where(d => d.BranchId == branchId.Value);
                }
                var devices = await devicesQuery.ToListAsync();

                int syncedDevicesCount = 0;
                List<string> logs = new List<string>();
                List<object> changesReport = new List<object>();

                foreach (var dev in devices)
                {
                    int port = dev.Port ?? 4370;
                    if (!IsTcpPortOpen(dev.Ipaddress, port, 1200))
                    {
                        logs.Add($"الجهاز غير متاح على الشبكة: {dev.DeviceName} ({dev.Ipaddress}:{port})");
                        continue;
                    }

                    try
                    {
                        CZKEM zk = new CZKEM();
                        if (zk.Connect_Net(dev.Ipaddress, port))
                        {
                            int updatedOnDevice = 0;
                            foreach (var item in updatedList)
                            {
                                string code = item["Code"];
                                string name = item["Name"];
                                string encodedName = HRSystem.Services.ZKTecoHelper.EncodeForZKTeco(name);

                                string pwd = ""; int priv = 0; bool enabled = true; string rawOldName = "";
                                bool found = zk.SSR_GetUserInfo(1, code, out rawOldName, out pwd, out priv, out enabled);
                                string oldNameDecoded = found ? HRSystem.Services.ZKTecoHelper.DecodeFromZKTeco(rawOldName) : "جديد / غير مسجل";

                                if (zk.SSR_SetUserInfo(1, code, encodedName, pwd, priv, enabled))
                                {
                                    updatedOnDevice++;
                                    changesReport.Add(new {
                                        code = code,
                                        deviceName = dev.DeviceName,
                                        oldNameOnDevice = oldNameDecoded,
                                        newNameOnDevice = name
                                    });
                                }
                            }
                            zk.RefreshData(1);
                            zk.Disconnect();
                            syncedDevicesCount++;
                            logs.Add($"تم تحديث {updatedOnDevice} اسم على الجهاز: {dev.DeviceName}");
                        }
                        else
                        {
                            logs.Add($"فشل الاتصال بالجهاز: {dev.DeviceName}");
                        }
                    }
                    catch (Exception ex)
                    {
                        logs.Add($"خطأ بالجهاز {dev.DeviceName}: {ex.Message}");
                    }
                }

                return Json(new {
                    success = true,
                    message = $"تم تحديث {updatedList.Count} موظف في قاعدة البيانات، وإرسال التعديلات لعدد {syncedDevicesCount} جهاز بصمة.",
                    changesReport = changesReport,
                    logs = logs
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "حدث خطأ أثناء الاستيراد والمزامنة: " + ex.Message });
            }
        }

        [HttpGet]
        public async Task<IActionResult> ManageNames()
        {
            ViewBag.Branches = await _context.Branches.Where(b => b.IsActive == true).ToListAsync();
            ViewBag.Devices = await _context.FingerDevices.Where(d => d.IsActive == true).ToListAsync();
            return View();
        }

        [HttpGet]
        public async Task<IActionResult> GetBranchDeviceUsers(int? branchId, int? deviceId)
        {
            try
            {
                FingerDevice device = null;
                if (deviceId.HasValue && deviceId.Value > 0)
                {
                    device = await _context.FingerDevices.FirstOrDefaultAsync(d => d.DeviceId == deviceId.Value && d.IsActive == true);
                }
                else if (branchId.HasValue && branchId.Value > 0)
                {
                    device = await _context.FingerDevices.FirstOrDefaultAsync(d => d.BranchId == branchId.Value && d.IsActive == true);
                }
                else
                {
                    device = await _context.FingerDevices.FirstOrDefaultAsync(d => d.IsActive == true);
                }

                if (device == null || string.IsNullOrEmpty(device.Ipaddress))
                {
                    return Json(new { success = false, message = "لم يتم العثور على جهاز بصمة نشط للفرع المحدد." });
                }

                int port = device.Port ?? 4370;
                CZKEM zk = new CZKEM();
                if (zk.Connect_Net(device.Ipaddress, port))
                {
                    zk.EnableDevice(1, false);
                    zk.ReadAllUserID(1);

                    string enrollNumber = "";
                    string name = "";
                    string password = "";
                    int privilege = 0;
                    bool enabled = false;

                    var userList = new List<object>();

                    while (zk.SSR_GetAllUserInfo(1, out enrollNumber, out name, out password, out privilege, out enabled))
                    {
                        string decodedName = HRSystem.Services.ZKTecoHelper.DecodeFromZKTeco(name);

                        userList.Add(new {
                            enrollNumber = enrollNumber,
                            nameOnDevice = decodedName,
                            privilege = privilege == 3 ? "مدير الجهاز" : "عادي",
                            enabled = enabled
                        });
                    }

                    zk.EnableDevice(1, true);
                    zk.Disconnect();

                    return Json(new {
                        success = true,
                        deviceName = device.DeviceName,
                        ipAddress = device.Ipaddress,
                        totalUsers = userList.Count,
                        users = userList
                    });
                }
                else
                {
                    return Json(new { success = false, message = $"تعذر الاتصال بجهاز البصمة: {device.DeviceName} ({device.Ipaddress})" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = "خطأ أثناء قراءة أسماء الموظفين من البصمة: " + ex.Message });
            }
        }
    }
}
