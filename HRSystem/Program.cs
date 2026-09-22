using HRSystem.Models;
using HRSystem.Services;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.DataProtection;

var builder = WebApplication.CreateBuilder(args);
builder.Host.UseWindowsService();

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddDbContext<HRContext>(options =>
    options.UseSqlServer(builder.Configuration.GetConnectionString("defaultconnection") ?? throw new InvalidOperationException("Connection string 'HRSystemContext' not found.")));

// ─── New HR Module Services ───────────────────────────────────────────────
builder.Services.AddHttpClient<HRSystem.Services.WhatsAppService>();
builder.Services.AddScoped<HRSystem.Services.LeaveService>();
builder.Services.AddScoped<HRSystem.Services.PayrollService>();


builder.Services.AddDataProtection()
    .PersistKeysToFileSystem(new System.IO.DirectoryInfo(System.IO.Path.Combine(builder.Environment.ContentRootPath, "App_Data", "Keys")));

builder.Services.AddIdentity<ApplicationUser, IdentityRole>(options =>
{
    options.Password.RequireDigit = false;
    options.Password.RequiredLength = 4;
    options.Password.RequireNonAlphanumeric = false;
    options.Password.RequireUppercase = false;
    options.Password.RequireLowercase = false;
})
    .AddEntityFrameworkStores<HRContext>()
    .AddDefaultTokenProviders();

builder.Services.ConfigureApplicationCookie(options =>
{
    options.LoginPath = "/Account/Login";
    options.LogoutPath = "/Account/Logout";
    options.ExpireTimeSpan = TimeSpan.FromDays(30); // الاحتفاظ بتسجيل الدخول لمدة 30 يوم
    options.SlidingExpiration = true; // تجديد الصلاحية تلقائياً عند التفاعل النشط مع الموقع
    options.Cookie.HttpOnly = true;
    options.Cookie.IsEssential = true;
    options.Cookie.Name = "HRSystem_AuthCookie";
});

builder.Services.AddHostedService<AttendanceService>();
builder.WebHost.UseUrls("http://*:5051");
var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var services = scope.ServiceProvider;
    await SeedData.Initialize(services);

    try
    {
        var context = services.GetRequiredService<HRContext>();
        var employees = await context.Employees.ToListAsync();
        bool isUpdated = false;
        foreach (var emp in employees)
        {
            if (HRSystem.Services.ZKTecoHelper.IsGarbled(emp.SearchName))
            {
                emp.SearchName = HRSystem.Services.ZKTecoHelper.DecodeFromZKTeco(emp.SearchName);
                emp.UpdatedAt = DateTime.Now;
                isUpdated = true;
            }
        }
        if (isUpdated)
        {
            await context.SaveChangesAsync();
        }
    }
    catch
    {
        // Ignore any errors during startup name cleanup
    }
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

// app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();

app.UseMiddleware<HRSystem.Services.SessionLogMiddleware>();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
