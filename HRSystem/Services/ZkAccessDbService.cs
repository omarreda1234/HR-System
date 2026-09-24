using System;
using System.Data.OleDb;
using System.IO;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace HRSystem.Services
{
    public class ZkAccessDbService
    {
        private readonly ILogger<ZkAccessDbService> _logger;

        public ZkAccessDbService(ILogger<ZkAccessDbService> logger)
        {
            _logger = logger;
        }

        private string BuildConnectionString(string mdbPath)
        {
            // Jet 4.0 is natively present on Windows for 32-bit (x86) processes
            return $@"Provider=Microsoft.Jet.OLEDB.4.0;Data Source={mdbPath};Persist Security Info=False;";
        }

        public async Task<(bool success, string message)> TestConnectionAsync(string mdbPath)
        {
            if (string.IsNullOrWhiteSpace(mdbPath))
            {
                return (false, "مسار قاعدة بيانات ZKTeco فارغ.");
            }

            mdbPath = mdbPath.Trim();

            if (!File.Exists(mdbPath))
            {
                return (false, $"الملف غير موجود أو يتعذر الوصول إليه عبر الشبكة. تأكد من صحة الآي بي والشير:\n{mdbPath}");
            }

            try
            {
                using var conn = new OleDbConnection(BuildConnectionString(mdbPath));
                await conn.OpenAsync();

                using var cmd = conn.CreateCommand();
                cmd.CommandText = "SELECT COUNT(*) FROM USERINFO";
                var count = await cmd.ExecuteScalarAsync();

                return (true, $"تم الاتصال بنجاح بقاعدة بيانات ZKTeco!\nعدد الموظفين المسجلين حالياً بالبرنامج: {count}");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Failed to connect to ZKTeco Access DB at {Path}", mdbPath);
                return (false, $"فشل فتح قاعدة بيانات الأكسيس: {ex.Message}");
            }
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

                return (true, $"تمت إضافة وتحديث الموظف '{userName}' بنجاح في قاعدة بيانات برنامج ZKTeco (Access)!");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error writing to ZKTeco Access DB at {Path}", mdbPath);
                return (false, $"خطأ أثناء الكتابة في قاعدة بيانات ZKTeco: {ex.Message}");
            }
        }
    }
}
