using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using System;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using HRSystem.Models;
using HRSystem.Services;

namespace HRSystem.Controllers
{
    public class DahuaController : Controller
    {
        private readonly HRContext _context;
        private static bool _isInitialized = false;
        private static readonly object _initLock = new object();

        public DahuaController(HRContext context)
        {
            _context = context;
        }

        private static void EnsureSDKInitialized()
        {
            if (!_isInitialized)
            {
                lock (_initLock)
                {
                    if (!_isInitialized)
                    {
                        try
                        {
                            // Configure Windows to search the DahuaSDK subfolder for peer DLLs of the SDK (like dhconfigsdk.dll)
                            string sdkPath = System.IO.Path.Combine(AppContext.BaseDirectory, "DahuaSDK");
                            if (!System.IO.Directory.Exists(sdkPath))
                            {
                                sdkPath = AppContext.BaseDirectory;
                            }
                            DahuaNetSDK.SetDllDirectory(sdkPath);
                        }
                        catch { }

                        _isInitialized = DahuaNetSDK.CLIENT_Init(null, IntPtr.Zero);
                        if (!_isInitialized)
                        {
                            throw new Exception("Failed to initialize Dahua NetSDK.");
                        }
                    }
                }
            }
        }

        // Dashboard displaying all branches and their DVR devices
        public async Task<IActionResult> Index()
        {
            var branches = await _context.Branches
                .Include(b => b.DvrDevices)
                .OrderBy(b => b.BranchName)
                .ToListAsync();

            return View(branches);
        }

        [HttpPost]
        public async Task<IActionResult> AddDvr(int branchId, string dvrName, string localIp, int vpnPort, string username, string password)
        {
            if (branchId == 0 || string.IsNullOrEmpty(dvrName) || string.IsNullOrEmpty(localIp) || vpnPort == 0 || string.IsNullOrEmpty(username) || string.IsNullOrEmpty(password))
            {
                return Json(new { success = false, message = "جميع الحقول مطلوبة." });
            }

            try
            {
                var dvr = new DvrDevice
                {
                    BranchId = branchId,
                    DvrName = dvrName,
                    LocalIp = localIp,
                    VpnPort = vpnPort,
                    Username = username,
                    Password = password,
                    Status = "لم يتم الفحص بعد"
                };

                _context.DvrDevices.Add(dvr);
                await _context.SaveChangesAsync();

                return Json(new { 
                    success = true, 
                    message = "تم إضافة جهاز الـ DVR بنجاح!",
                    dvr = new {
                        id = dvr.Id,
                        dvrName = dvr.DvrName,
                        localIp = dvr.LocalIp,
                        vpnPort = dvr.VpnPort,
                        status = dvr.Status,
                        lastSync = "أبداً"
                    }
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"فشل إضافة الجهاز: {ex.Message}" });
            }
        }

        [HttpPost]
        public async Task<IActionResult> DeleteDvr(int id)
        {
            var dvr = await _context.DvrDevices.FindAsync(id);
            if (dvr == null)
            {
                return Json(new { success = false, message = "جهاز الـ DVR غير موجود." });
            }

            try
            {
                _context.DvrDevices.Remove(dvr);
                await _context.SaveChangesAsync();
                return Json(new { success = true, message = "تم حذف جهاز الـ DVR بنجاح." });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"فشل حذف الجهاز: {ex.Message}" });
            }
        }

        [HttpPost]
        public async Task<IActionResult> TestDvrConnection(int id)
        {
            var dvr = await _context.DvrDevices.Include(d => d.Branch).FirstOrDefaultAsync(d => d.Id == id);
            if (dvr == null)
            {
                return Json(new { success = false, message = "جهاز الـ DVR غير موجود." });
            }

            var branchVpnIp = dvr.Branch?.VpnIp;
            string ipToConnect = string.IsNullOrEmpty(branchVpnIp) ? dvr.LocalIp : branchVpnIp;
            ushort port = (ushort)dvr.VpnPort;

            try
            {
                EnsureSDKInitialized();
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"فشل تهيئة الـ SDK: {ex.Message}" });
            }

            var deviceInfo = new DahuaNetSDK.NET_DEVICEINFO_Ex();
            int errorCode = 0;

            // Connect using designated IP (VPN IP or Local IP fallback) and the specific Port
            IntPtr loginId = DahuaNetSDK.CLIENT_LoginEx2(ipToConnect, port, dvr.Username, dvr.Password, 0, IntPtr.Zero, ref deviceInfo, ref errorCode);

            if (loginId == IntPtr.Zero)
            {
                int lastError = DahuaNetSDK.CLIENT_GetLastError();
                string errorDesc = GetErrorDescription(lastError);
                
                dvr.Status = "فشل الاتصال";
                _context.Update(dvr);
                await _context.SaveChangesAsync();

                return Json(new { 
                    success = false, 
                    message = $"فشل الاتصال بـ {ipToConnect}:{port}. السبب: {errorDesc}" 
                });
            }

            // Connection successful! Extract details
            string serialNumber = Encoding.ASCII.GetString(deviceInfo.sSerialNumber ?? new byte[0]).Trim('\0');
            int channels = deviceInfo.nChanNum;
            int disks = deviceInfo.nDiskNum;

            // Update status and last sync time
            dvr.Status = "متصل";
            dvr.LastSyncTime = DateTime.Now;
            _context.Update(dvr);
            await _context.SaveChangesAsync();

            // Logout
            DahuaNetSDK.CLIENT_Logout(loginId);

            return Json(new {
                success = true,
                message = "تم الاتصال بنجاح وقراءة بيانات الـ DVR!",
                data = new {
                    serialNumber = serialNumber,
                    channelCount = channels,
                    diskCount = disks,
                    deviceType = deviceInfo.nDVRType,
                    lastSync = dvr.LastSyncTime?.ToString("yyyy-MM-dd HH:mm:ss")
                }
            });
        }

        [HttpGet]
        public async Task<IActionResult> GetDvrTime(int id)
        {
            var dvr = await _context.DvrDevices.Include(d => d.Branch).FirstOrDefaultAsync(d => d.Id == id);
            if (dvr == null) return Json(new { success = false, message = "جهاز الـ DVR غير موجود." });

            var branchVpnIp = dvr.Branch?.VpnIp;
            string ipToConnect = string.IsNullOrEmpty(branchVpnIp) ? dvr.LocalIp : branchVpnIp;
            ushort port = (ushort)dvr.VpnPort;

            try { EnsureSDKInitialized(); }
            catch (Exception ex) { return Json(new { success = false, message = $"فشل تهيئة الـ SDK: {ex.Message}" }); }

            var deviceInfo = new DahuaNetSDK.NET_DEVICEINFO_Ex();
            int errorCode = 0;
            IntPtr loginId = DahuaNetSDK.CLIENT_LoginEx2(ipToConnect, port, dvr.Username, dvr.Password, 0, IntPtr.Zero, ref deviceInfo, ref errorCode);

            if (loginId == IntPtr.Zero)
            {
                int lastError = DahuaNetSDK.CLIENT_GetLastError();
                return Json(new { success = false, message = $"فشل الاتصال بالـ DVR لقراءة الوقت. السبب: {GetErrorDescription(lastError)}" });
            }

            try
            {
                var devTime = new DahuaNetSDK.NET_TIME();
                bool success = DahuaNetSDK.CLIENT_QueryDeviceTime(loginId, ref devTime, 3000);
                if (!success)
                {
                    int lastError = DahuaNetSDK.CLIENT_GetLastError();
                    return Json(new { success = false, message = $"فشل قراءة وقت الـ DVR. السبب: {GetErrorDescription(lastError)}" });
                }

                var deviceTimeVal = new DateTime(devTime.dwYear, devTime.dwMonth, devTime.dwDay, devTime.dwHour, devTime.dwMinute, devTime.dwSecond);
                var serverTimeVal = DateTime.Now;

                var deviceDateTimeStr = deviceTimeVal.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
                var serverDateTimeStr = serverTimeVal.ToString("yyyy-MM-dd HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

                var deviceDateTimeFormatted = deviceTimeVal.ToString("yyyy-MM-dd hh:mm:ss tt", System.Globalization.CultureInfo.InvariantCulture);
                var serverDateTimeFormatted = serverTimeVal.ToString("yyyy-MM-dd hh:mm:ss tt", System.Globalization.CultureInfo.InvariantCulture);

                return Json(new { 
                    success = true, 
                    deviceTime = deviceDateTimeStr,
                    serverTime = serverDateTimeStr,
                    deviceTimeFormatted = deviceDateTimeFormatted,
                    serverTimeFormatted = serverDateTimeFormatted
                });
            }
            finally
            {
                DahuaNetSDK.CLIENT_Logout(loginId);
            }
        }

        [HttpPost]
        public async Task<IActionResult> SyncDvrTime(int id)
        {
            var dvr = await _context.DvrDevices.Include(d => d.Branch).FirstOrDefaultAsync(d => d.Id == id);
            if (dvr == null) return Json(new { success = false, message = "جهاز الـ DVR غير موجود." });

            var branchVpnIp = dvr.Branch?.VpnIp;
            string ipToConnect = string.IsNullOrEmpty(branchVpnIp) ? dvr.LocalIp : branchVpnIp;
            ushort port = (ushort)dvr.VpnPort;

            try { EnsureSDKInitialized(); }
            catch (Exception ex) { return Json(new { success = false, message = $"فشل تهيئة الـ SDK: {ex.Message}" }); }

            var deviceInfo = new DahuaNetSDK.NET_DEVICEINFO_Ex();
            int errorCode = 0;
            IntPtr loginId = DahuaNetSDK.CLIENT_LoginEx2(ipToConnect, port, dvr.Username, dvr.Password, 0, IntPtr.Zero, ref deviceInfo, ref errorCode);

            if (loginId == IntPtr.Zero)
            {
                int lastError = DahuaNetSDK.CLIENT_GetLastError();
                return Json(new { success = false, message = $"فشل الاتصال بالـ DVR لضبط الوقت. السبب: {GetErrorDescription(lastError)}" });
            }

            try
            {
                var now = DateTime.Now;
                var newTime = new DahuaNetSDK.NET_TIME
                {
                    dwYear = now.Year,
                    dwMonth = now.Month,
                    dwDay = now.Day,
                    dwHour = now.Hour,
                    dwMinute = now.Minute,
                    dwSecond = now.Second
                };

                bool success = DahuaNetSDK.CLIENT_SetupDeviceTime(loginId, ref newTime);
                if (!success)
                {
                    int lastError = DahuaNetSDK.CLIENT_GetLastError();
                    return Json(new { success = false, message = $"فشل ضبط وقت الـ DVR. السبب: {GetErrorDescription(lastError)}" });
                }

                return Json(new { 
                    success = true, 
                    message = $"تم ضبط وتزامن وقت الـ DVR مع وقت السيرفر بنجاح! الوقت الجديد: {now:yyyy-MM-dd HH:mm:ss}" 
                });
            }
            finally
            {
                DahuaNetSDK.CLIENT_Logout(loginId);
            }
        }

        [HttpPost]
        public async Task<IActionResult> AddBranch(string branchName, string branchCode, string vpnIp, string city)
        {
            if (string.IsNullOrEmpty(branchName) || string.IsNullOrEmpty(branchCode))
            {
                return Json(new { success = false, message = "كود الفرع واسم الفرع مطلوبان." });
            }

            var codeExists = await _context.Branches.AnyAsync(b => b.BranchCode == branchCode);
            if (codeExists)
            {
                return Json(new { success = false, message = "كود الفرع مستخدم بالفعل لفرع آخر." });
            }

            try
            {
                var branch = new Branch
                {
                    BranchName = branchName,
                    BranchCode = branchCode,
                    VpnIp = vpnIp,
                    City = city,
                    IsActive = true
                };

                _context.Branches.Add(branch);
                await _context.SaveChangesAsync();

                return Json(new { 
                    success = true, 
                    message = "تم إضافة الفرع بنجاح!", 
                    branch = new {
                        branchId = branch.BranchId,
                        branchCode = branch.BranchCode,
                        branchName = branch.BranchName,
                        vpnIp = branch.VpnIp ?? ""
                    }
                });
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"فشل إضافة الفرع: {ex.Message}" });
            }
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

        [HttpPost]
        public async Task<IActionResult> UpdateBranchVpnIp(int branchId, string vpnIp)
        {
            var branch = await _context.Branches.FindAsync(branchId);
            if (branch == null) return Json(new { success = false, message = "الفرع غير موجود." });

            branch.VpnIp = vpnIp;
            _context.Update(branch);
            await _context.SaveChangesAsync();

            return Json(new { success = true, message = "تم تحديث IP الـ VPN بنجاح." });
        }

        [HttpPost]
        public async Task<IActionResult> RunProxyCommandsOnServer(int id)
        {
            var dvr = await _context.DvrDevices.Include(d => d.Branch).FirstOrDefaultAsync(d => d.Id == id);
            if (dvr == null) return Json(new { success = false, message = "جهاز الـ DVR غير موجود." });

            var branchVpnIp = dvr.Branch?.VpnIp;
            string listenIp = string.IsNullOrEmpty(branchVpnIp) ? "0.0.0.0" : branchVpnIp;
            string connectIp = dvr.LocalIp;
            int port = dvr.VpnPort;

            try
            {
                var netshInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c netsh interface portproxy add v4tov4 listenaddress={listenIp} listenport={port} connectaddress={connectIp} connectport={port}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var netshProcess = System.Diagnostics.Process.Start(netshInfo);
                string netshOutput = await netshProcess.StandardOutput.ReadToEndAsync();
                string netshError = await netshProcess.StandardError.ReadToEndAsync();
                await netshProcess.WaitForExitAsync();

                var fwInfo = new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c netsh advfirewall firewall add rule name=\"Dahua DVR Proxy {port}\" dir=in action=allow protocol=TCP localport={port}",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var fwProcess = System.Diagnostics.Process.Start(fwInfo);
                string fwOutput = await fwProcess.StandardOutput.ReadToEndAsync();
                string fwError = await fwProcess.StandardError.ReadToEndAsync();
                await fwProcess.WaitForExitAsync();

                if (netshProcess.ExitCode == 0 && fwProcess.ExitCode == 0)
                {
                    return Json(new { success = true, message = "تم تشغيل الأوامر على السيرفر بنجاح!" });
                }
                else
                {
                    string errMsg = string.IsNullOrEmpty(netshError) ? fwError : netshError;
                    return Json(new { success = false, message = $"فشل التشغيل. تأكد من تشغيل السيرفر بصلاحيات المسؤول (Administrator). التفاصيل: {errMsg}" });
                }
            }
            catch (Exception ex)
            {
                return Json(new { success = false, message = $"حدث خطأ أثناء محاولة تشغيل الأوامر: {ex.Message}" });
            }
        }

        private string GetErrorDescription(int errorCode)
        {
            // Clear the high bit (0x80000000) if it is set to get the base error code
            int baseError = errorCode;
            if (errorCode < 0)
            {
                baseError = (int)(errorCode & 0x7FFFFFFF);
            }

            switch (baseError)
            {
                case 1:
                case 100: return "اسم المستخدم أو كلمة المرور غير صحيحة (Wrong Password)";
                case 2:
                case 102: return "انتهت مهلة الاتصال بالـ DVR (Timeout)";
                case 3:
                case 103: return "الجهاز قيد الاستخدام بالفعل أو المستخدم مسجل الدخول بالفعل";
                case 4:
                case 107: return "الجهاز مغلق أو غير متصل بالشبكة";
                case 5: return "الوصول غير مسموح به";
                case 7:
                case 108: return "تجاوز الحد الأقصى للمستخدمين المتصلين";
                case 101: return "اسم المستخدم غير موجود بالـ DVR";
                case 104: return "الحساب مغلق مؤقتاً بالـ DVR بسبب محاولات خاطئة متكررة";
                case 105: return "المستخدم محظور (Blacklisted)";
                default: return $"خطأ اتصال بالشبكة أو بالـ DVR (كود: {baseError})";
            }
        }
    }
}
