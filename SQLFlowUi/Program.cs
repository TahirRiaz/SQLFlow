using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.OData;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.JSInterop;
using Microsoft.OData.ModelBuilder;
using Radzen;
using SQLFlowUi.Components;
using SQLFlowUi.Controllers;
using SQLFlowUi.Data;
using SQLFlowUi.Models;
using SQLFlowUi.Service;
using SQLFlowUi.Services;
using System;

var builder = WebApplication.CreateBuilder(args);

// Get connection string (supports both environment variable and configuration)
var connectionString = Environment.GetEnvironmentVariable("SQLFlowConStr")
    ?? builder.Configuration.GetConnectionString("sqlflowProdConnection")
    ?? throw new InvalidOperationException("Connection string not found. Please provide either 'SQLFlowConStr' environment variable or 'sqlflowProdConnection' in configuration.");



// Configure application cookie settings
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
    .AddHubOptions(options => options.MaximumReceiveMessageSize = 10 * 1024 * 1024);

// Configure controllers
builder.Services.AddControllers();

// Add UI and client services
builder.Services.AddRadzenComponents();
builder.Services.AddRadzenCookieThemeService(options =>
{
    options.Name = "SQLFlowUiTheme";
    options.Duration = TimeSpan.FromDays(365);
});
builder.Services.AddHttpClient();

// Add scoped services
builder.Services.AddScoped<ConfigService>();
builder.Services.AddScoped<DialogService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<TooltipService>();
builder.Services.AddScoped<ContextMenuService>();
builder.Services.AddScoped<DocumentationService>();
builder.Services.AddScoped<SQLFlowUi.sqlflowProdService>();
builder.Services.AddScoped<IUserInformationService, HttpService>();
builder.Services.AddScoped<IHttpService, HttpService>();
builder.Services.AddScoped<SQLFlowUi.SecurityService>();
builder.Services.AddScoped<AuthenticationStateProvider, SQLFlowUi.ApplicationAuthenticationStateProvider>();
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


app.UseStaticFiles();

// Configure middleware pipeline in the correct order
var provider = new FileExtensionContentTypeProvider();
provider.Mappings[".css"] = "text/css";
provider.Mappings[".js"] = "application/javascript";
provider.Mappings[".woff"] = "font/woff";
provider.Mappings[".woff2"] = "font/woff2";
provider.Mappings[".ttf"] = "font/ttf";
provider.Mappings[".svg"] = "image/svg+xml";
provider.Mappings[".ico"] = "image/x-icon";

app.UseStaticFiles(new StaticFileOptions
{
    ContentTypeProvider = provider,
    OnPrepareResponse = ctx =>
    {
        // Explicitly set content type for CSS files regardless of query string
        if (ctx.File.Name.EndsWith(".css", StringComparison.OrdinalIgnoreCase))
        {
            ctx.Context.Response.ContentType = "text/css";
        }
    }
});


// Add security headers middleware
app.Use(async (context, next) =>
{
    context.Response.Headers.Append("X-Content-Type-Options","nosniff");
    context.Response.Headers.Append("X-Frame-Options","DENY");
    context.Response.Headers.Append("X-XSS-Protection", "1; mode=block");
    await next();
});

// Configure forwarded headers early in the pipeline
app.UseForwardedHeaders(new ForwardedHeadersOptions
{
    ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto,
    ForwardLimit = null
});

// Configure error handling for production
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts(); // Enable HTTP Strict Transport Security
}

// Apply HTTPS redirection based on environment
if (!app.Environment.IsDevelopment())
{
    app.UseHttpsRedirection();
}

app.UseRouting(); // Must come before authentication/authorization
app.UseHeaderPropagation();
app.UseAuthentication();
app.UseAuthorization();
app.UseAntiforgery();

// Map endpoints
app.MapControllers();
app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

// Database migrations (uncomment if you want to automatically run migrations)
// app.Services.CreateScope().ServiceProvider.GetRequiredService<ApplicationIdentityDbContext>().Database.Migrate();

app.Run();