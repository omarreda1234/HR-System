using System;
using System.Collections.Generic;
using System.Data.OleDb;
using System.IO;
using System.Net.Sockets;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace HRSystem.Services
{
    public class DiagnosticStep
    {
        public string Title { get; set; } = "";
        public bool Passed { get; set; }
        public string Details { get; set; } = "";
        public string Recommendation { get; set; } = "";
    }

    public class ZkDiagnosticResult
    {
        public bool Success { get; set; }
        public string TargetPath { get; set; } = "";
        public string ExtractedIp { get; set; } = "";
        public string Summary { get; set; } = "";
        public List<DiagnosticStep> Steps { get; set; } = new();
    }

    public class ZkUserInfoItem
    {
        public long UserId { get; set; }
        public string BadgeNumber { get; set; } = "";
        public string Name { get; set; } = "";
        public string? CardNo { get; set; }
    }

    public class ZkAccessDbService
    {
        private readonly ILogger<ZkAccessDbService> _logger;

        public ZkAccessDbService(ILogger<ZkAccessDbService> logger)
        {
            _logger = logger;
        }

        private string BuildConnectionString(string mdbPath)
        {
            return $@"Provider=Microsoft.Jet.OLEDB.4.0;Data Source={mdbPath};Persist Security Info=False;";
        }

        public async Task<ZkDiagnosticResult> DiagnoseConnectionAsync(string rawPath)
        {
            var result = new ZkDiagnosticResult();
            if (string.IsNullOrWhiteSpace(rawPath))
            {
                result.Success = false;
                result.Summary = "لم يتم إدخال مسار للفحص.";
                return result;
            }

            string cleanPath = rawPath.Trim();
            result.TargetPath = cleanPath;

            // 1. Check Format (UNC vs Local)
            var stepFormat = new DiagnosticStep { Title = "فحص صيغة المسار" };
            if (!cleanPath.StartsWith(@"\\"))
            {
                stepFormat.Passed = false;
                stepFormat.Details = $"المسار المدخل يبدو كمسار محلي: '{cleanPath}'.";
                stepFormat.Recommendation = @"السيرفر لا يمكنه فتح مسار محلي C:\ الخاص بجهاز كمبيوتر آخر. يجب كتابة المسار بصيغة مسار شبكة يبدأ بـ \\ مثل: \\172.16.9.60\ZKTeco\att2000.mdb";
                result.Steps.Add(stepFormat);
                result.Success = false;
                result.Summary = "صيغة المسار غير صحيحة (يجب أن يبدأ بـ \\)";
                return result;
            }
            else
            {
                stepFormat.Passed = true;
                stepFormat.Details = "صيغة مسار الشبكة UNC صحيحة تبدأ بـ \\\\";
                result.Steps.Add(stepFormat);
            }

            // Extract IP or Hostname
            var match = Regex.Match(cleanPath, @"^\\\\([^\\]+)");
            string ipOrHost = match.Success ? match.Groups[1].Value : "";
            result.ExtractedIp = ipOrHost;

            // 2. Check PC Reachability (Port 3389 / Port 445)
            var stepHost = new DiagnosticStep { Title = $"الوصول لجهاز الكمبيوتر بالفرع ({ipOrHost})" };
            bool rdpOpen = await CheckTcpPortAsync(ipOrHost, 3389, 1500);
            bool smbOpen = await CheckTcpPortAsync(ipOrHost, 445, 1500);

            if (rdpOpen || smbOpen)
            {
                stepHost.Passed = true;
                stepHost.Details = $"جهاز الفرع متصل بنجاح على الشبكة (RDP: {(rdpOpen ? "مفتوح" : "مغلق")} - SMB: {(smbOpen ? "مفتوح" : "مغلق")}).";
            }
            else
            {
                stepHost.Passed = false;
                stepHost.Details = $"تعذر الوصول لجهاز الكمبيوتر ({ipOrHost}) على الشبكة.";
                stepHost.Recommendation = "تأكد من أن جهاز الكمبيوتر بالفرع مفتوح ومتصل بالـ VPN ولا يوجد انقطاع في الإنترنت هناك.";
            }
            result.Steps.Add(stepHost);

            // 3. Check SMB File Sharing Port (445)
            var stepSmb = new DiagnosticStep { Title = "منفذ مشاركة الملفات (Port 445 - SMB)" };
            if (smbOpen)
            {
                stepSmb.Passed = true;
                stepSmb.Details = "منفذ مشاركة الملفات 445 مفتوح في جدار الحماية (Firewall) وجاهز لنقل البيانات.";
            }
            else
            {
                stepSmb.Passed = false;
                stepSmb.Details = "منفذ مشاركة الملفات 445 مغلق أو محجوب في جدار الحماية (Windows Defender Firewall) بجهاز الفرع.";
                stepSmb.Recommendation = "قم بفتح PowerShell كمسؤول (Run as Administrator) على جهاز الفرع ونفذ الأمر:\n" +
                                          "netsh advfirewall firewall set rule group=\"File and Printer Sharing\" new enable=Yes";
            }
            result.Steps.Add(stepSmb);

            // 4. Check File Existence
            var stepFile = new DiagnosticStep { Title = "الوصول لملف قاعدة البيانات (att2000.mdb)" };
            bool fileExists = false;
            try
            {
                fileExists = File.Exists(cleanPath);
            }
            catch (Exception ex)
            {
                stepFile.Details = $"خطأ أمني أو في الصلاحيات أثناء فحص الملف: {ex.Message}";
            }

            if (fileExists)
            {
                stepFile.Passed = true;
                stepFile.Details = "تم العثور على ملف قاعدة بيانات ZKTeco بنجاح وقراءته عبر مسار الشير.";
            }
            else
            {
                stepFile.Passed = false;
                if (string.IsNullOrEmpty(stepFile.Details))
                {
                    stepFile.Details = $"الملف غير موجود في المسار المحدد: {cleanPath}";
                }
                stepFile.Recommendation = "تأكد من اسم الشير واسم الملف (مثلاً تأكد أن مجلد ZKTeco معمول له Share باسم ZKTeco، وأن اسم الملف att2000.mdb).";
            }
            result.Steps.Add(stepFile);

            // 5. Check OleDb Jet 4.0 Connection
            if (fileExists)
            {
                var stepDb = new DiagnosticStep { Title = "قراءة بيانات ZKTeco (OLEDB Jet 4.0)" };
                try
                {
                    using var conn = new OleDbConnection(BuildConnectionString(cleanPath));
                    await conn.OpenAsync();

                    using var cmd = conn.CreateCommand();
                    cmd.CommandText = "SELECT COUNT(*) FROM USERINFO";
                    var count = await cmd.ExecuteScalarAsync();

                    stepDb.Passed = true;
                    stepDb.Details = $"الاتصال بقاعدة بيانات ZKTeco ناجح بنسبة 100%! عدد الموظفين المسجلين حالياً: {count}";
                    result.Steps.Add(stepDb);

                    result.Success = true;
                    result.Summary = $"تم الاتصال بنجاح بقاعدة بيانات ZKTeco! عدد الموظفين: {count}";
                    return result;
                }
                catch (Exception ex)
                {
                    stepDb.Passed = false;
                    stepDb.Details = $"فشل فتح ملف الأكسيس: {ex.Message}";
                    stepDb.Recommendation = "تأكد من عدم وجود كلمة مرور لقاعدة بيانات Access أو أن الملف غير تالف.";
                    result.Steps.Add(stepDb);
                }
            }

            result.Success = false;
            result.Summary = "فشل في أحد خطوات الاتصال (يرجى مراجعة تفاصيل التقرير أدناه).";
            return result;
        }

        private async Task<bool> CheckTcpPortAsync(string host, int port, int timeoutMs)
        {
            if (string.IsNullOrWhiteSpace(host)) return false;
            using var client = new TcpClient();
            try
            {
                var task = client.ConnectAsync(host, port);
                var completed = await Task.WhenAny(task, Task.Delay(timeoutMs));
                return completed == task && client.Connected;
            }
            catch
            {
                return false;
            }
        }

        public async Task<(bool success, string message)> TestConnectionAsync(string mdbPath)
        {
            var diag = await DiagnoseConnectionAsync(mdbPath);
            return (diag.Success, diag.Summary);
        }

        public async Task<List<ZkUserInfoItem>> GetUsersAsync(string mdbPath, string? search = null, int limit = 50)
        {
            var list = new List<ZkUserInfoItem>();
            if (string.IsNullOrWhiteSpace(mdbPath) || !File.Exists(mdbPath)) return list;

            try
            {
                using var conn = new OleDbConnection(BuildConnectionString(mdbPath));
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();
                string query = "SELECT TOP " + limit + " USERID, Badgenumber, Name, CardNo FROM USERINFO";
                if (!string.IsNullOrWhiteSpace(search))
                {
                    query += " WHERE Badgenumber LIKE @s OR Name LIKE @s";
                    cmd.Parameters.AddWithValue("@s", "%" + search.Trim() + "%");
                }
                query += " ORDER BY USERID DESC";
                cmd.CommandText = query;

                using var reader = await cmd.ExecuteReaderAsync();
                while (await reader.ReadAsync())
                {
                    list.Add(new ZkUserInfoItem
                    {
                        UserId = Convert.ToInt64(reader["USERID"]),
                        BadgeNumber = reader["Badgenumber"]?.ToString() ?? "",
                        Name = reader["Name"]?.ToString() ?? "",
                        CardNo = reader["CardNo"]?.ToString()
                    });
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to load users from ZK Access DB {Path}", mdbPath);
            }
            return list;
        }

        public async Task<(bool success, string message)> AddOrUpdateUserInAccessDbAsync(
            string mdbPath, 
            string userCode, 
            string userName, 
            string? deviceIp = null)
        {
            if (string.IsNullOrWhiteSpace(mdbPath))
            {
                return (false, "مسار قاعدة بيانات ZKTeco غير محدد لهذا الفرع.");
            }

            mdbPath = mdbPath.Trim();

            if (!File.Exists(mdbPath))
            {
                return (false, $"تعذر الوصول لملف قاعدة بيانات ZKTeco بالفرع:\n{mdbPath}");
            }

            try
            {
                using var conn = new OleDbConnection(BuildConnectionString(mdbPath));
                await conn.OpenAsync();

                // 1. Check if user already exists
                using var checkCmd = conn.CreateCommand();
                checkCmd.CommandText = "SELECT USERID, Name FROM USERINFO WHERE Badgenumber = @badge";
                checkCmd.Parameters.AddWithValue("@badge", userCode);

                object? existingUserIdObj = null;
                using (var reader = await checkCmd.ExecuteReaderAsync())
                {
                    if (await reader.ReadAsync())
                    {
                        existingUserIdObj = reader["USERID"];
                    }
                }

                long userId;
                if (existingUserIdObj != null)
                {
                    userId = Convert.ToInt64(existingUserIdObj);

                    using var updateCmd = conn.CreateCommand();
                    updateCmd.CommandText = "UPDATE USERINFO SET Name = @name WHERE USERID = @uid";
                    updateCmd.Parameters.AddWithValue("@name", userName);
                    updateCmd.Parameters.AddWithValue("@uid", userId);
                    await updateCmd.ExecuteNonQueryAsync();
                }
                else
                {
                    using var insertCmd = conn.CreateCommand();
                    insertCmd.CommandText = @"INSERT INTO USERINFO (Badgenumber, Name, DEFAULTDEPTID, PRIVILEGE, InheritDeptSch, InheritDeptSchClass, AutoSchPlan) 
                                             VALUES (@badge, @name, 1, 0, 1, 1, 1)";
                    insertCmd.Parameters.AddWithValue("@badge", userCode);
                    insertCmd.Parameters.AddWithValue("@name", userName);
                    await insertCmd.ExecuteNonQueryAsync();

                    using var idCmd = conn.CreateCommand();
                    idCmd.CommandText = "SELECT @@IDENTITY";
                    var idResult = await idCmd.ExecuteScalarAsync();
                    userId = Convert.ToInt64(idResult);
                }

                // 2. Link to machine in UsersMachines if machine is defined in Access DB
                if (!string.IsNullOrEmpty(deviceIp))
                {
                    try
                    {
                        using var machCmd = conn.CreateCommand();
                        machCmd.CommandText = "SELECT ID FROM Machines WHERE IP = @ip";
                        machCmd.Parameters.AddWithValue("@ip", deviceIp.Trim());
                        var machIdObj = await machCmd.ExecuteScalarAsync();

                        if (machIdObj != null)
                        {
                            int machId = Convert.ToInt32(machIdObj);

                            using var checkUmCmd = conn.CreateCommand();
                            checkUmCmd.CommandText = "SELECT COUNT(*) FROM UsersMachines WHERE USERID = @uid AND DEVICEID = @did";
                            checkUmCmd.Parameters.AddWithValue("@uid", userId);
                            checkUmCmd.Parameters.AddWithValue("@did", machId);
                            int count = Convert.ToInt32(await checkUmCmd.ExecuteScalarAsync());

                            if (count == 0)
                            {
                                using var insUmCmd = conn.CreateCommand();
                                insUmCmd.CommandText = "INSERT INTO UsersMachines (USERID, DEVICEID) VALUES (@uid, @did)";
                                insUmCmd.Parameters.AddWithValue("@uid", userId);
                                insUmCmd.Parameters.AddWithValue("@did", machId);
                                await insUmCmd.ExecuteNonQueryAsync();
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "Could not link user {UserId} in UsersMachines for IP {Ip}", userId, deviceIp);
                    }
                }

                // 3. Add to UserUpdates if table exists
                try
                {
                    using var updCmd = conn.CreateCommand();
                    updCmd.CommandText = "INSERT INTO UserUpdates (BadgeNumber) VALUES (@badge)";
                    updCmd.Parameters.AddWithValue("@badge", userCode);
                    await updCmd.ExecuteNonQueryAsync();
                }
                catch
                {
                    // Ignore if UserUpdates not present in this Access file
                }

                return (true, $"تمت إضافة وتحديث الموظف '{userName}' (كود: {userCode}) بنجاح في قاعدة بيانات برنامج ZKTeco (Access)!");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error writing to ZKTeco Access DB at {Path}", mdbPath);
                return (false, $"خطأ أثناء الكتابة في قاعدة بيانات ZKTeco: {ex.Message}");
            }
        }
    }
}
