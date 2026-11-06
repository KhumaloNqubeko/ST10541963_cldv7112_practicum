using AgriStream.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddControllersWithViews();
builder.Services.AddScoped<AgriStream.Services.FarmAccess>();
builder.Services.AddSingleton(TimeProvider.System);
builder.Services.AddHttpClient<AgriStream.Services.FieldTelemetryClient>(http => http.Timeout = TimeSpan.FromSeconds(5))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddScoped<AgriStream.Services.FarmerTelemetryDashboard>();
builder.Services.AddRazorPages();
builder.Services.AddDbContext<ApplicationDbContext>(o => o.UseSqlServer(
    builder.Configuration.GetConnectionString("DefaultConnection") ?? throw new InvalidOperationException("Configure ConnectionStrings:DefaultConnection for Azure SQL."), sql => sql.EnableRetryOnFailure()));
builder.Services.AddDefaultIdentity<IdentityUser>(o => {
    o.Password.RequiredLength = 12;
    o.SignIn.RequireConfirmedAccount = false;
    o.Lockout.MaxFailedAccessAttempts = 5;
}).AddRoles<IdentityRole>().AddEntityFrameworkStores<ApplicationDbContext>();
builder.Services.ConfigureApplicationCookie(o => o.Cookie.SecurePolicy = CookieSecurePolicy.Always);
// Accounts are provisioned by administrators; disable public registration endpoints.
builder.Services.AddAuthorization(o => o.AddPolicy("NoPublicRegistration", p => p.RequireAssertion(_ => false)));
builder.Services.Configure<Microsoft.AspNetCore.Mvc.RazorPages.RazorPagesOptions>(o => {
    o.Conventions.AuthorizeAreaPage("Identity", "/Account/Register", "NoPublicRegistration");
    o.Conventions.AuthorizeAreaPage("Identity", "/Account/RegisterConfirmation", "NoPublicRegistration");
});

var app = builder.Build();
if (args.Contains("--seed-demo-data")) {
    using var scope = app.Services.CreateScope();
    await DemoDataSeeder.SeedAsync(scope.ServiceProvider, builder.Configuration);
    return;
}
if (args.Contains("--initialize-database")) {
    using var scope = app.Services.CreateScope();
    await DatabaseInitializer.InitializeAsync(scope.ServiceProvider, builder.Configuration);
    return;
}

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Home/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseRouting();

app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

app.MapStaticAssets();

app.MapControllerRoute(
    name: "default",
    pattern: "{controller=Home}/{action=Index}/{id?}")
    .WithStaticAssets();


app.Run();
