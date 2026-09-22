using System.Runtime.InteropServices;
using HRSystem.Models;
using Microsoft.EntityFrameworkCore;
using zkemkeeper;

namespace HRSystem.Services
{
    public class AttendanceService : BackgroundService
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<AttendanceService> _logger;
        private readonly IConfiguration _config;

        public static DateTime? LastAutoSyncTime { get; private set; }
        public static int LastSyncedDeviceCount { get; private set; }
        public static int TotalActiveDeviceCount { get; private set; }
        public static string LastSyncStatus { get; private set; } = "لم يبدأ بعد";

        public AttendanceService(IServiceProvider serviceProvider, ILogger<AttendanceService> logger, IConfiguration config)
        {
            _serviceProvider = serviceProvider;
            _logger = logger;
            _config = config;
        }

        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            _logger.LogInformation("ZKTeco Attendance Background Service started.");

            // الانتظار لمدة 5 ثوانٍ عند بدء تشغيل السيرفر للتأكد من جاهزية قاعدة البيانات والتطبيق
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), stoppingToken);
            }
            catch (TaskCanceledException)
            {
                return;
            }

            while (!stoppingToken.IsCancellationRequested)
            {
                // فترة التكرار الافتراضية دقيقتين بدلاً من 6 ساعات
                int intervalMinutes = _config.GetValue<int>("AttendanceSync:IntervalMinutes", 2);
                if (intervalMinutes < 1) intervalMinutes = 2;

                int timeoutSeconds = _config.GetValue<int>("AttendanceSync:TimeoutSeconds", 2);
                if (timeoutSeconds < 1) timeoutSeconds = 2;

                try
                {
                    _logger.LogInformation("Starting ZKTeco Attendance auto-sync loop (Interval: {Minutes} min)...", intervalMinutes);
                    LastSyncStatus = "جاري السحب...";

                    List<int> activeDeviceIds;
                    using (var initScope = _serviceProvider.CreateScope())
                    {
                        var context = initScope.ServiceProvider.GetRequiredService<HRContext>();
                        activeDeviceIds = await context.FingerDevices
                            .AsNoTracking()
                            .Where(d => d.IsActive == true && !string.IsNullOrEmpty(d.Ipaddress))
                            .Select(d => d.DeviceId)
                            .ToListAsync(stoppingToken);
                    }

                    TotalActiveDeviceCount = activeDeviceIds.Count;
                    int successfulSyncs = 0;

                    foreach (var deviceId in activeDeviceIds)
                    {
                        if (stoppingToken.IsCancellationRequested) break;

                        try
                        {
                            bool success = await SyncSingleDeviceAsync(deviceId, timeoutSeconds, stoppingToken);
                            if (success) successfulSyncs++;
                        }
                        catch (Exception ex)
                        {
                            _logger.LogError(ex, "Error occurred while syncing device ID {DeviceId}", deviceId);
                        }
                    }

                    LastAutoSyncTime = DateTime.Now;
                    LastSyncedDeviceCount = successfulSyncs;
                    LastSyncStatus = $"تم بنجاح ({successfulSyncs} من {activeDeviceIds.Count} أجهزة)";
                    _logger.LogInformation("ZKTeco Attendance auto-sync finished. Synced {Count}/{Total} devices.", successfulSyncs, activeDeviceIds.Count);
                }
                catch (Exception ex)
                {
                    LastSyncStatus = "خطأ في دورة السحب: " + ex.Message;
                    _logger.LogError(ex, "Error in ZKTeco Attendance synchronization loop");
                }

                try
                {
                    await Task.Delay(TimeSpan.FromMinutes(intervalMinutes), stoppingToken);
                }
                catch (TaskCanceledException)
                {
                    break;
                }
            }
        }

        private async Task<bool> SyncSingleDeviceAsync(int deviceId, int timeoutSeconds, CancellationToken stoppingToken)
        {
            var syncLock = DeviceSyncLock.GetLock(deviceId);
            // إذا كان الجهاز قيد السحب يدوياً أو مشغولاً حالياً، نتخطاه دون تعليق
            if (!await syncLock.WaitAsync(0, stoppingToken))
            {
                _logger.LogDebug("Device {DeviceId} is currently busy with another operation. Skipping.", deviceId);
                return false;
            }

            try
            {
                using var scope = _serviceProvider.CreateScope();
                var context = scope.ServiceProvider.GetRequiredService<HRContext>();

                var deviceRecord = await context.FingerDevices.FindAsync(new object[] { deviceId }, stoppingToken);
                if (deviceRecord == null || string.IsNullOrEmpty(deviceRecord.Ipaddress))
                {
                    return false;
                }

                int port = deviceRecord.Port ?? 4370;

                // فحص سريع مسبق (Pre-flight TCP Check) بمهلة قصيرة لتجنب تجميد الكود عند انقطاع اتصال الفرع
                bool isReachable = await IsPortReachableAsync(deviceRecord.Ipaddress, port, timeoutSeconds * 1000);
                if (!isReachable)
                {
                    _logger.LogDebug("Device {DeviceId} ({Name}) at {Ip}:{Port} is unreachable. Skipped.",
                        deviceId, deviceRecord.DeviceName, deviceRecord.Ipaddress, port);
                    return false;
                }

                CZKEM? device = null;
                try
                {
                    device = new CZKEM();
                    if (!device.Connect_Net(deviceRecord.Ipaddress, port))
                    {
                        return false;
                    }

                    device.EnableDevice(1, false);

                    if (device.ReadGeneralLogData(1))
                    {
                        string enrollNumber = "";
                        int verifyMode = 0, inOutMode = 0, year = 0, month = 0, day = 0, hour = 0, minute = 0, second = 0, workCode = 0;
                        int newLogsCount = 0;

                        // وقت بداية السحب الآمن (استخدام تاريخ آمن لـ SQL Server)
                        var lastSync = deviceRecord.LastSyncTime ?? new DateTime(1900, 1, 1);
                        var fetchStart = lastSync > new DateTime(1900, 1, 1) ? lastSync.AddDays(-1) : lastSync;

                        // كاش الموظفين المتاحين مع دعم مطابقة الأكواد المرنة (بدون أصفار بادئة)
                        var employees = await context.Employees.AsNoTracking().Select(e => e.No).ToListAsync(stoppingToken);
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

                        // تحضير الـ Keys المسجلة مسبقاً لمنع التكرار
                        var existingKeys = await context.Attendances
                            .Where(a => a.DeviceId == deviceId && a.CheckTime >= fetchStart)
                            .Select(a => a.EmployeeCode + "|" + a.CheckTime.Ticks)
                            .ToListAsync(stoppingToken);
                        var existingSet = new HashSet<string>(existingKeys);

                        while (device.SSR_GetGeneralLogData(1, out enrollNumber, out verifyMode, out inOutMode,
                               out year, out month, out day, out hour, out minute, out second, ref workCode))
                        {
                            enrollNumber = enrollNumber.Trim();
                            if (string.IsNullOrEmpty(enrollNumber)) continue;

                            DateTime logDate;
                            try { logDate = new DateTime(year, month, day, hour, minute, second); } catch { continue; }

                            if (logDate <= fetchStart) continue;

                            string? exactEmployeeCode = null;
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
                                    empName = ZKTecoHelper.DecodeFromZKTeco(empName);
                                }

                                if (string.IsNullOrEmpty(empName))
                                {
                                    empName = "موظف غير مسجل " + enrollNumber;
                                }

                                string safeCode = enrollNumber.Length > 20 ? enrollNumber.Substring(0, 20) : enrollNumber;
                                string safeName = empName.Length > 100 ? empName.Substring(0, 100) : empName;

                                // التحقق من عدم وجوده مسبقاً قبل الإضافة
                                if (!await context.Employees.AnyAsync(e => e.No == safeCode, stoppingToken))
                                {
                                    var newEmp = new Employee
                                    {
                                        No = safeCode,
                                        SearchName = safeName,
                                        BranchId = deviceRecord.BranchId,
                                        Disabled = false,
                                        UpdatedAt = DateTime.Now
                                    };

                                    context.Employees.Add(newEmp);
                                }

                                employeeMap[enrollNumber] = safeCode;
                                exactEmployeeCode = safeCode;
                            }

                            var key = exactEmployeeCode + "|" + logDate.Ticks;
                            if (existingSet.Contains(key)) continue;

                            context.Attendances.Add(new Attendance
                            {
                                EmployeeCode = exactEmployeeCode,
                                CheckTime = logDate,
                                CheckType = inOutMode == 0 ? "In" : "Out",
                                DeviceId = deviceId,
                                VerifyMode = verifyMode
                            });
                            existingSet.Add(key);
                            newLogsCount++;
                        }

                        if (newLogsCount > 0)
                        {
                            await context.SaveChangesAsync(stoppingToken);
                        }

                        // تحديث وقت آخر مزامنة للجهاز
                        deviceRecord.LastSyncTime = DateTime.Now;
                        await context.SaveChangesAsync(stoppingToken);

                        _logger.LogInformation("Device {DeviceId} ({Name}): Synced {NewLogs} new records successfully.",
                            deviceId, deviceRecord.DeviceName, newLogsCount);
                    }
                    else
                    {
                        // حتى في حال عدم وجود سجلات جديدة، طالما تم الاتصال نحدث LastSyncTime
                        deviceRecord.LastSyncTime = DateTime.Now;
                        await context.SaveChangesAsync(stoppingToken);
                    }

                    device.EnableDevice(1, true);
                    return true;
                }
                finally
                {
                    if (device != null)
                    {
                        try { device.Disconnect(); } catch { }
                        try { Marshal.ReleaseComObject(device); } catch { }
                    }
                }
            }
            finally
            {
                syncLock.Release();
            }
        }

        private static async Task<bool> IsPortReachableAsync(string ip, int port, int timeoutMs)
        {
            try
            {
                using var client = new System.Net.Sockets.TcpClient();
                var connectTask = client.ConnectAsync(ip, port);
                var delayTask = Task.Delay(timeoutMs);

                var completedTask = await Task.WhenAny(connectTask, delayTask);
                if (completedTask == connectTask && client.Connected)
                {
                    return true;
                }
                return false;
            }
            catch
            {
                return false;
            }
        }
    }
}