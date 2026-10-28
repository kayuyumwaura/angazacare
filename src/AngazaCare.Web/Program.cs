using AngazaCare.Data;
using AngazaCare.Domain;
using AngazaCare.Services;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Npgsql;

var builder = WebApplication.CreateBuilder(args);
var config = builder.Configuration;

// Data: one pooled Npgsql data source, raw SQL repositories.
var connString = config.GetConnectionString("Angaza") ?? throw new InvalidOperationException("ConnectionStrings:Angaza is not set.");
builder.Services.AddSingleton(NpgsqlDataSource.Create(connString));
builder.Services.AddSingleton<Db>();
builder.Services.AddScoped<UserRepo>();
builder.Services.AddScoped<ConfigRepo>();
builder.Services.AddScoped<MotherRepo>();
builder.Services.AddScoped<MessageRepo>();
builder.Services.AddScoped<FlagRepo>();
builder.Services.AddScoped<MetricsRepo>();
builder.Services.AddScoped<DbInitializer>();

// Encryption keys for phone numbers and cookies. Back this folder up: losing it makes stored phone numbers unreadable.
var keyPath = config["DataProtection:KeyPath"] ?? Path.Combine(builder.Environment.ContentRootPath, "keys");
builder.Services.AddDataProtection().PersistKeysToFileSystem(new DirectoryInfo(keyPath)).SetApplicationName("AngazaCare");
builder.Services.AddSingleton<PhoneProtector>();

// Care logic.
var care = config.GetSection("Care").Get<CareOptions>() ?? new CareOptions();
builder.Services.AddSingleton(care);
builder.Services.AddSingleton<RulesCache>();
builder.Services.AddScoped<Outbox>();
builder.Services.AddScoped<FlagWorkflow>();
builder.Services.AddScoped<InboundService>();
builder.Services.AddScoped<CheckInService>();
builder.Services.AddHostedService<CareWorker>();

// SMS: the simulator by default; Africa's Talking when configured.
if (string.Equals(config["Sms:Gateway"], "africastalking", StringComparison.OrdinalIgnoreCase))
    builder.Services.AddHttpClient<ISmsGateway, AfricasTalkingGateway>(c => c.Timeout = TimeSpan.FromSeconds(15));
else
    builder.Services.AddSingleton<ISmsGateway, SimulatorGateway>();

// The AI parsing service is optional. Without it, replies the rules cannot read go straight to a person.
builder.Services.AddHttpClient<LlmClient>(c =>
{
    if (config["AiService:BaseUrl"] is { Length: > 0 } url) c.BaseAddress = new Uri(url.TrimEnd('/') + "/");
    c.Timeout = TimeSpan.FromSeconds(config.GetValue("AiService:TimeoutSeconds", 10));
});

builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(o =>
    {
        o.LoginPath = "/account/login";
        o.AccessDeniedPath = "/account/denied";
        o.ExpireTimeSpan = TimeSpan.FromDays(14);   // CHWs work offline in the field; keep them signed in
        o.SlidingExpiration = true;
        o.Cookie.Name = "angaza";
        o.Cookie.HttpOnly = true;
        o.Cookie.SameSite = SameSiteMode.Lax;
    });
builder.Services.AddAuthorization(o =>
{
    o.AddPolicy("Chw", p => p.RequireRole(Roles.Chw));
    o.AddPolicy("Clinical", p => p.RequireRole(Roles.Nurse, Roles.Admin));
    o.AddPolicy("Admin", p => p.RequireRole(Roles.Admin));
});
builder.Services.AddControllersWithViews(o => o.Filters.Add(new Microsoft.AspNetCore.Mvc.AutoValidateAntiforgeryTokenAttribute()));

var app = builder.Build();

using (var scope = app.Services.CreateScope())
    await scope.ServiceProvider.GetRequiredService<DbInitializer>().RunAsync();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/error");
    app.UseHsts();
}
app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapControllerRoute("default", "{controller=Home}/{action=Index}/{id?}");
app.Run();

public partial class Program;
