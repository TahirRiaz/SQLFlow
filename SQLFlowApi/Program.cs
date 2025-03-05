using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Microsoft.EntityFrameworkCore;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.OData;
using Microsoft.OData.ModelBuilder;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.SqlServer.Types;
using System.Reflection;
using System.Text;
using System.Text.Json.Serialization;

using SQLFlowApi;
using SQLFlowApi.Data;
using SQLFlowApi.Models;
using SQLFlowApi.Services;
using SQLFlowUi.Service;
using SQLFlowApi.Controllers;

var builder = WebApplication.CreateBuilder(args);
var configuration = builder.Configuration;
var environment = builder.Environment;

// Initialize configuration and services
var sqlFlowConfig = new ConfigService().configSettings;

// Ensure SqlGeography is properly initialized
SqlGeography.Null.ToString();

// Configure authentication and cookie policies
ConfigureAuthentication(builder, sqlFlowConfig);

// Configure CORS
ConfigureCors(builder);

// Configure data protection
ConfigureDataProtection(builder);

// Configure API controllers and OData
ConfigureControllers(builder);

// Configure Swagger
ConfigureSwagger(builder);

// Configure background services
ConfigureBackgroundServices(builder, sqlFlowConfig);

// Configure HTTP request options
ConfigureHttpOptions(builder);

// Build the application
var app = builder.Build();

// Configure the HTTP request pipeline
ConfigurePipeline(app, environment);

// Run the application
app.Run();

// ====== Configuration Methods ======

void ConfigureAuthentication(WebApplicationBuilder builder, ConfigSettings sqlFlowConfig)
{
    // Configure Cookie Policy
    builder.Services.ConfigureApplicationCookie(options =>
    {
        options.Cookie.SameSite = environment.IsDevelopment() ? SameSiteMode.Lax : SameSiteMode.Strict; 
        options.Cookie.SecurePolicy = environment.IsDevelopment()
            ? CookieSecurePolicy.None
            : CookieSecurePolicy.Always;
        options.Cookie.HttpOnly = true;
        options.ExpireTimeSpan = TimeSpan.FromHours(24);
    });

    // Configure JWT Authentication
    builder.Services.AddAuthentication(options =>
    {
        options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
        options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
    })
    .AddJwtBearer(options =>
    {
        options.RequireHttpsMetadata = !environment.IsDevelopment();
        options.SaveToken = true;
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidateAudience = true,
            ValidateLifetime = true,
            ValidateIssuerSigningKey = true,
            ValidIssuer = sqlFlowConfig.Issuer,
            ValidAudience = sqlFlowConfig.Audience,
            IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(sqlFlowConfig.SecretKey)),
            ClockSkew = TimeSpan.Zero
        };

        // Add logging for authentication events in development
        if (environment.IsDevelopment())
        {
            options.Events = new JwtBearerEvents
            {
                OnAuthenticationFailed = context =>
                {
                    Console.WriteLine($"Authentication failed: {context.Exception.Message}");
                    return Task.CompletedTask;
                },
                OnTokenValidated = context =>
                {
                    Console.WriteLine("Token successfully validated");
                    return Task.CompletedTask;
                }
            };
        }
    });

    // Add Identity
    builder.Services.AddDbContext<ApplicationIdentityDbContext>(options =>
    {
        string connectionString = Environment.GetEnvironmentVariable("SQLFlowConStr") ?? string.Empty;
        options.UseSqlServer(connectionString);
    });

    builder.Services.AddIdentity<ApplicationUser, ApplicationRole>()
        .AddEntityFrameworkStores<ApplicationIdentityDbContext>()
        .AddDefaultTokenProviders();

    // Add services for authentication
    builder.Services.AddScoped<TokenService>();
    builder.Services.AddScoped<AuthenticationStateProvider, SQLFlowApi.ApplicationAuthenticationStateProvider>();
}

void ConfigureCors(WebApplicationBuilder builder)
{
    builder.Services.AddCors(options =>
    {
        options.AddDefaultPolicy(policy =>
        {
            policy.AllowAnyMethod()
                  .AllowAnyHeader()
                  .SetIsOriginAllowed(origin => true) // Consider restricting this in production
                  .AllowCredentials();
        });
    });
}

void ConfigureDataProtection(WebApplicationBuilder builder)
{
    if (environment.IsDevelopment())
    {
        // Development environment - use ephemeral in-memory key provider
        Console.WriteLine("Running in Development mode with in-memory Data Protection key");

        builder.Services.AddDataProtection()
            .SetApplicationName("SQLFlowApi")
            .UseEphemeralDataProtectionProvider();

        // Development antiforgery configuration
        builder.Services.AddAntiforgery(options =>
        {
            options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
            options.Cookie.SameSite = SameSiteMode.Lax;
            options.HeaderName = "X-CSRF-TOKEN";
        });
    }
    else
    {
        // In production, you should use a persistent key provider
        // Consider configuring proper key storage for production
        builder.Services.AddDataProtection()
            .SetApplicationName("SQLFlowApi");
        // Add production-specific key persistence here
    }
}

void ConfigureControllers(WebApplicationBuilder builder)
{
    // Add HTTP client
    builder.Services.AddHttpClient();

    // Add controllers with JSON options
    builder.Services.AddControllers()
        .AddJsonOptions(options =>
        {
            options.JsonSerializerOptions.Converters.Add(new JsonStringEnumConverter());
        });

    // Add OData support
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

    // Add service dependencies
    builder.Services.AddScoped<ConfigService>();
    builder.Services.AddScoped<SecurityService>();
}

void ConfigureSwagger(WebApplicationBuilder builder)
{
    builder.Services.AddEndpointsApiExplorer();
    builder.Services.AddSwaggerGen(options =>
    {
        // Set the comments path for the Swagger JSON and UI
        var xmlFile = $"{Assembly.GetExecutingAssembly().GetName().Name}.xml";
        var xmlPath = Path.Combine(AppContext.BaseDirectory, xmlFile);
        options.IncludeXmlComments(xmlPath);

        // Enable annotations
        options.EnableAnnotations();

        // Exclude OData paths from Swagger
        options.DocInclusionPredicate((docName, apiDesc) =>
        {
            return !apiDesc.RelativePath?.StartsWith("odata") ?? true;
        });

        // Add security definition for JWT
        options.AddSecurityDefinition("Bearer", new Microsoft.OpenApi.Models.OpenApiSecurityScheme
        {
            Name = "Authorization",
            Type = Microsoft.OpenApi.Models.SecuritySchemeType.Http,
            Scheme = "bearer",
            BearerFormat = "JWT",
            In = Microsoft.OpenApi.Models.ParameterLocation.Header,
            Description = "JWT Authorization header using the Bearer scheme."
        });

        options.AddSecurityRequirement(new Microsoft.OpenApi.Models.OpenApiSecurityRequirement
        {
            {
                new Microsoft.OpenApi.Models.OpenApiSecurityScheme
                {
                    Reference = new Microsoft.OpenApi.Models.OpenApiReference
                    {
                        Type = Microsoft.OpenApi.Models.ReferenceType.SecurityScheme,
                        Id = "Bearer"
                    }
                },
                new string[] {}
            }
        });
    });
}

void ConfigureBackgroundServices(WebApplicationBuilder builder, ConfigSettings sqlFlowConfig)
{
    int maxParallelTasks = sqlFlowConfig.MaxParallelTasks;
    int maxParallelSteps = sqlFlowConfig.MaxParallelSteps;

    builder.Services.AddSingleton<PipelineBackgroundService>(provider =>
    {
        var logger = provider.GetRequiredService<ILogger<PipelineBackgroundService>>();
        return new PipelineBackgroundService(logger, maxParallelTasks, maxParallelSteps);
    });

    builder.Services.AddHostedService(provider => provider.GetRequiredService<PipelineBackgroundService>());
}

void ConfigureHttpOptions(WebApplicationBuilder builder)
{
    // Configure Kestrel
    builder.WebHost.ConfigureKestrel(serverOptions =>
    {
        serverOptions.Limits.MaxRequestHeaderCount = 400;
        serverOptions.Limits.MaxRequestHeadersTotalSize = 65536; // 64 KB
        serverOptions.Limits.KeepAliveTimeout = TimeSpan.FromMinutes(720);
        serverOptions.AllowSynchronousIO = true;
    });

    // Configure IIS options
    builder.Services.Configure<IISServerOptions>(options =>
    {
        options.AllowSynchronousIO = true;
    });

    // Configure forwarded headers
    builder.Services.Configure<ForwardedHeadersOptions>(options =>
    {
        options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
        options.ForwardLimit = null;
    });
}

void ConfigurePipeline(WebApplication app, IWebHostEnvironment environment)
{
    // Configure forwarded headers middleware
    app.UseForwardedHeaders();

    // Add security headers middleware here
    app.Use(async (context, next) =>
    {
        context.Response.Headers.Add("X-Content-Type-Options", "nosniff");
        context.Response.Headers.Add("X-Frame-Options", "DENY");
        context.Response.Headers.Add("X-XSS-Protection", "1; mode=block");
        await next();
    });

    // Configure Swagger in development
    app.UseSwagger();
    app.UseSwaggerUI();

    // Configure HTTPS redirection in production
    if (!environment.IsDevelopment())
    {
        app.UseHttpsRedirection();
    }

    // Configure diagnostic middleware in development
    if (environment.IsDevelopment())
    {
        app.Use(async (context, next) =>
        {
            var endpoint = context.GetEndpoint();
            Console.WriteLine($"Request Path: {context.Request.Path}, Endpoint: {endpoint?.DisplayName}");
            await next();
            Console.WriteLine($"Response Status Code: {context.Response.StatusCode}");
        });
    }

    // Use CORS
    app.UseCors();

    // Serve static files
    app.UseStaticFiles();

    // Configure routing
    app.UseRouting();

    // Configure authentication and authorization
    app.UseAuthentication();
    app.UseAuthorization();

    // Map controllers
    app.MapControllers();
}