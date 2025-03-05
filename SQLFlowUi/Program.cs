using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.OData;
using Microsoft.EntityFrameworkCore;
using Microsoft.OData.ModelBuilder;
using Radzen;
using SQLFlowUi.Controllers;
using SQLFlowUi.Data;
using SQLFlowUi.Models;
using SQLFlowUi.Service;
using SQLFlowUi.Services;
using GaelJ.BlazorCodeMirror6;
using SQLFlowUi.Components;
using System;

var builder = WebApplication.CreateBuilder(args);

// Get connection string
var connectionString = Environment.GetEnvironmentVariable("SQLFlowConStr")
    ?? throw new InvalidOperationException("Connection string 'SQLFlowConStr' not found in environment variables.");


builder.Services.ConfigureApplicationCookie(options =>
{
    options.Cookie.SameSite = builder.Environment.IsDevelopment() ? SameSiteMode.Lax : SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.None
        : CookieSecurePolicy.Always;
    options.Cookie.HttpOnly = true;
    options.ExpireTimeSpan = TimeSpan.FromHours(24);
});

// Configure Razor components and server-side interactivity
builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents()
    .AddHubOptions(options => options.MaximumReceiveMessageSize = 10 * 1024 * 1024); // Corrected size calculation

// Configure controllers
builder.Services.AddControllers();

// Add UI and client services
builder.Services.AddRadzenComponents();
builder.Services.AddHttpClient();

// Add scoped services
builder.Services.AddScoped<ConfigService>();
builder.Services.AddScoped<Radzen.DialogService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<TooltipService>();
builder.Services.AddScoped<ContextMenuService>();
builder.Services.AddScoped<DocumentationService>();
builder.Services.AddScoped<SQLFlowUi.sqlflowProdService>();
builder.Services.AddScoped<IUserInformationService, HttpService>();
builder.Services.AddScoped<IHttpService, HttpService>();
builder.Services.AddScoped<SQLFlowUi.SecurityService>();
builder.Services.AddScoped<AuthenticationStateProvider, SQLFlowUi.ApplicationAuthenticationStateProvider>();
builder.Services.AddScoped<CodeMirror6Wrapper>();
builder.Services.AddScoped<SQLFlowGraphService>();

// Configure database contexts
builder.Services.AddDbContext<SQLFlowUi.Data.sqlflowProdContext>(options =>
{
    options.UseSqlServer(connectionString);
});

builder.Services.AddDbContext<ApplicationIdentityDbContext>(options =>
{
    options.UseSqlServer(connectionString);
});

// Configure HTTP client with header propagation
builder.Services.AddHttpClient("SQLFlowUi")
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { UseCookies = false })
    .AddHeaderPropagation(o => o.Headers.Add("Cookie"));

builder.Services.AddHeaderPropagation(o => o.Headers.Add("Cookie"));

// Configure authentication and authorization
builder.Services.AddAuthentication();
builder.Services.AddAuthorization();
builder.Services.AddHttpContextAccessor();

// Configure Identity
builder.Services.AddIdentity<ApplicationUser, ApplicationRole>()
    .AddEntityFrameworkStores<ApplicationIdentityDbContext>()
    .AddDefaultTokenProviders();

// Configure OData
builder.Services.AddControllers().AddOData(o =>
{
    var oDataBuilder = new ODataConventionModelBuilder();
    oDataBuilder.EntitySet<ApplicationUser>("ApplicationUsers");
    var usersType = oDataBuilder.StructuralTypes.First(x => x.ClrType == typeof(ApplicationUser));
    usersType.AddProperty(typeof(ApplicationUser).GetProperty(nameof(ApplicationUser.Password)));
    usersType.AddProperty(typeof(ApplicationUser).GetProperty(nameof(ApplicationUser.ConfirmPassword)));
    oDataBuilder.EntitySet<ApplicationRole>("ApplicationRoles");
    o.AddRouteComponents("odata/Identity", oDataBuilder.GetEdmModel())
     .Count().Filter().OrderBy().Expand().Select().SetMaxTop(null)
     .TimeZone = TimeZoneInfo.Utc;
});

var app = builder.Build();

// Add security headers middleware right here
app.Use(async (context, next) =>
{
    context.Response.Headers.Add("X-Content-Type-Options", "nosniff");
    context.Response.Headers.Add("X-Frame-Options", "DENY");
    context.Response.Headers.Add("X-XSS-Protection", "1; mode=block");
    await next();
});

// Configure forwarded headers early in the pipeline
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = null // No limit on the number of forwarded headers to trust
});

// Configure error handling for production
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts(); // Enable HTTP Strict Transport Security
}

// Apply HTTPS redirection based on configuration
if (builder.Environment.IsDevelopment() == false)
{
    app.UseHttpsRedirection();
}

// Configure middleware pipeline in the correct order
app.UseStaticFiles(); // Must be called before UseRouting
app.UseRouting(); // Must come before authentication/authorization
app.UseHeaderPropagation();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Map endpoints
app.MapControllers();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// Uncomment to automatically run migrations on startup (use with caution in production)
// app.Services.CreateScope().ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>().Database.Migrate();

app.Run();