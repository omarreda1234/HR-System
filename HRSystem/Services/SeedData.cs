using HRSystem.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace HRSystem.Services;

public static class SeedData
{
    public static async Task Initialize(IServiceProvider serviceProvider)
    {
        var context = serviceProvider.GetRequiredService<HRContext>();
        
        // Ensure UserSessionLogs table and DvrDevices table exist automatically
        try
        {
            await context.Database.ExecuteSqlRawAsync(@"
                IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='UserSessionLogs' and xtype='U')
                BEGIN
                    CREATE TABLE UserSessionLogs (
                        Id INT IDENTITY(1,1) PRIMARY KEY,
                        Email NVARCHAR(256) NULL,
                        IpAddress NVARCHAR(50) NULL,
                        ComputerName NVARCHAR(256) NULL,
                        DeviceDetails NVARCHAR(MAX) NULL,
                        AccessTime DATETIME NOT NULL,
                        ActionAccessed NVARCHAR(256) NULL
                    )
                END

                IF NOT EXISTS (SELECT * FROM sys.columns WHERE object_id = OBJECT_ID('Branches') AND name = 'VpnIp')
                BEGIN
                    ALTER TABLE Branches ADD VpnIp NVARCHAR(50) NULL;
                END

                IF NOT EXISTS (SELECT * FROM sysobjects WHERE name='DvrDevices' and xtype='U')
                BEGIN
                    CREATE TABLE DvrDevices (
                        Id INT IDENTITY(1,1) PRIMARY KEY,
                        BranchId INT NOT NULL,
                        DvrName NVARCHAR(100) NOT NULL,
                        LocalIp NVARCHAR(50) NOT NULL,
                        VpnPort INT NOT NULL DEFAULT 3777,
                        Username NVARCHAR(100) NOT NULL,
                        Password NVARCHAR(100) NOT NULL,
                        LastSyncTime DATETIME NULL,
                        Status NVARCHAR(50) NULL,
                        CONSTRAINT FK_DvrDevices_Branches FOREIGN KEY (BranchId) REFERENCES Branches(BranchId) ON DELETE CASCADE
                    )
                END
            ");
        }
        catch { }

        // Seed Branches and VPN IPs
        var branchData = new System.Collections.Generic.Dictionary<string, string>
        {
            { "aga", "172.16.35.100" },
            { "alex", "172.16.49.100" },
            { "bank", "172.16.14.100" },
            { "bata", "172.16.34.100" },
            { "BELQ ASMAA", "172.16.24.100" },
            { "BELQ HAMDY", "172.16.33.100" },
            { "BELQ NADIA", "172.16.32.100" },
            { "BELQ SAHAR", "172.16.10.100" },
            { "DKRNS", "172.16.11.100" },
            { "DOMM", "172.16.31.100" },
            { "DOMMD", "172.16.50.100" },
            { "DOMN", "172.16.42.100" },
            { "DOMNZ", "172.16.48.100" },
            { "EMAN", "172.16.9.100" },
            { "GESH", "172.16.20.100" },
            { "GHAMR", "172.16.22.100" },
            { "HEGAZY", "172.16.23.100" },
            { "HELAL", "172.16.16.100" },
            { "H-UNI", "172.16.47.100" },
            { "KAFR", "172.16.26.100" },
            { "KAMALO", "172.16.28.100" },
            { "MAD", "172.16.45.100" },
            { "MAHER", "172.16.36.100" },
            { "MANAKH", "172.16.2.100" },
            { "MASHAAL", "172.16.3.100" },
            { "MASRY", "172.16.29.100" },
            { "MATAFY", "172.16.19.100" },
            { "PORTF", "172.16.41.100" },
            { "PORTFT", "26.83.232.141" },
            { "QAZAZ", "26.180.208.197" },
            { "RASHED", "172.16.30.100" },
            { "SAMIA", "172.16.8.100" },
            { "SEEM", "172.16.25.100" },
            { "seen", "172.16.1.100" },
            { "SEKA", "172.16.5.100" },
            { "SHERBEN", "172.16.21.100" },
            { "SILK", "172.16.13.100" },
            { "TAHA", "172.16.6.100" },
            { "TALKHA", "172.16.15.100" },
            { "TANTA", "172.16.27.100" },
            { "TANTAD", "26.200.145.21" },
            { "TANTAS", "172.16.46.100" },
            { "TERAA", "172.16.7.100" },
            { "TOMEHE", "172.16.4.100" },
            { "TOREL", "172.16.17.100" },
            { "UNI", "172.16.12.100" }
        };

        foreach (var item in branchData)
        {
            var code = item.Key.ToUpper().Replace(" ", "_");
            var branch = await context.Branches.FirstOrDefaultAsync(b => b.BranchCode == code || b.BranchName == item.Key);
            if (branch == null)
            {
                branch = new Branch
                {
                    BranchCode = code,
                    BranchName = item.Key,
                    City = "Egypt",
                    IsActive = true,
                    VpnIp = item.Value
                };
                context.Branches.Add(branch);
            }
            else
            {
                branch.VpnIp = item.Value;
                context.Entry(branch).State = EntityState.Modified;
            }
        }
        await context.SaveChangesAsync();

        var userManager = serviceProvider.GetRequiredService<UserManager<ApplicationUser>>();
        var roleManager = serviceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        
        // 1. Ensure Roles exist
        string[] roleNames = { "SuperAdmin", "HR", "BranchAdmin" };
        foreach (var roleName in roleNames)
        {
            if (!await roleManager.RoleExistsAsync(roleName))
            {
                await roleManager.CreateAsync(new IdentityRole(roleName));
            }
        }

        // 2. Seed SuperAdmin
        var superEmail = "super45@gmail.com";
        var superUser = await userManager.FindByEmailAsync(superEmail);
        if (superUser == null)
        {
            superUser = new ApplicationUser { UserName = superEmail, Email = superEmail, EmailConfirmed = true };
            await userManager.CreateAsync(superUser, "Super@2468");
            await userManager.AddToRoleAsync(superUser, "SuperAdmin");
        }

        // 3. Seed HR User
        var hrEmail = "HR45@gmail.com";
        var hrUser = await userManager.FindByEmailAsync(hrEmail);
        if (hrUser == null)
        {
            hrUser = new ApplicationUser { UserName = hrEmail, Email = hrEmail, EmailConfirmed = true };
            await userManager.CreateAsync(hrUser, "HR@2468");
            await userManager.AddToRoleAsync(hrUser, "HR");
        }

        // 4. Seed BranchAdmins for each branch
        var branches = await context.Branches.ToListAsync();
        foreach (var branch in branches)
        {
            var branchEmail = $"branch{branch.BranchId}@gmail.com";
            // If branch has no ID yet (should not happen with ToListAsync), we skip or use code
            if (branch.BranchId == 0) continue;

            var branchAdmin = await userManager.FindByEmailAsync(branchEmail);
            if (branchAdmin == null)
            {
                branchAdmin = new ApplicationUser 
                { 
                    UserName = branchEmail, 
                    Email = branchEmail, 
                    EmailConfirmed = true,
                    BranchId = branch.BranchId 
                };
                var result = await userManager.CreateAsync(branchAdmin, "Test@2468");
                if (result.Succeeded)
                {
                    await userManager.AddToRoleAsync(branchAdmin, "BranchAdmin");
                }
            }
        }
    }
}
