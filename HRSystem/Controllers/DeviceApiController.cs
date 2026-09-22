using HRSystem.Models;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using zkemkeeper;

namespace HRSystem.Controllers
{
    [Route("api/[controller]")]
    [ApiController]
    public class DeviceApiController : ControllerBase
    {
        private readonly HRContext _context;
        private readonly IConfiguration _config;

        public DeviceApiController(HRContext context, IConfiguration config)
        {
            _context = context;
            _config = config;
        }

        /// <summary>
        /// سحب البيانات من جهاز بصمة محدد
        /// URL: POST /api/DeviceApi/Sync
        /// Header: x-api-key: [SecretKey]
        /// Body (JSON): { "deviceId": 1 }
        /// </summary>
        [HttpPost("Sync")]
        public async Task<IActionResult> Sync([FromBody] SyncRequest request)
        {
            // 1. التحقق من الـ API Key للأمان
            var secretKey = _config["ApiSettings:SecretKey"];
            if (!Request.Headers.TryGetValue("x-api-key", out var extractedKey) || extractedKey != secretKey)
            {
                return Unauthorized(new { message = "Invalid or missing API Key" });
            }

            var deviceRecord = await _context.FingerDevices.FindAsync(request.DeviceId);
            if (deviceRecord == null || string.IsNullOrEmpty(deviceRecord.Ipaddress))
            {
                return NotFound(new { message = "Device not found" });
            }

            int port = deviceRecord.Port ?? 4370;
            try
            {
                CZKEM device = new CZKEM();
                if (device.Connect_Net(deviceRecord.Ipaddress, port))
                {
                    int newLogsCount = 0;
                    device.EnableDevice(1, false);
                    if (device.ReadGeneralLogData(1))
                    {
                        string enrollNumber = "";
                        int verifyMode = 0, inOutMode = 0, year = 0, month = 0, day = 0, hour = 0, minute = 0, second = 0, workCode = 0;

                        var lastSync = deviceRecord.LastSyncTime ?? new DateTime(1900, 1, 1);
                        var fetchStart = lastSync > new DateTime(1900, 1, 1) ? lastSync.AddDays(-1) : lastSync;
                        
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
                        
                        var existingKeys = await _context.Attendances
                            .Where(a => a.DeviceId == request.DeviceId && a.CheckTime >= fetchStart)
                            .Select(a => a.EmployeeCode + "|" + a.CheckTime.Ticks)
                            .ToListAsync();
                        var existingSet = new HashSet<string>(existingKeys);

                        while (device.SSR_GetGeneralLogData(1, out enrollNumber, out verifyMode, out inOutMode,
                               out year, out month, out day, out hour, out minute, out second, ref workCode))
                        {
                            enrollNumber = enrollNumber.Trim();
                            DateTime logDate;
                            try { logDate = new DateTime(year, month, day, hour, minute, second); } catch { continue; }
                            
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
                                DeviceId = request.DeviceId,
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
                    }
                    device.EnableDevice(1, true);
                    device.Disconnect();
                    return Ok(new { success = true, newLogs = newLogsCount, message = $"Synced {newLogsCount} logs." });
                }
                return BadRequest(new { message = "Could not connect to device" });
            }
            catch (Exception ex)
            {
                return StatusCode(500, new { message = ex.Message });
            }
        }
    }

    public class SyncRequest
    {
        public int DeviceId { get; set; }
    }
}
