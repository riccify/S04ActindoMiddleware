using System.Security.Claims;
using ActindoMiddleware.Application.Monitoring;
using ActindoMiddleware.Application.Configuration;
using ActindoMiddleware.Application.Security;
using ActindoMiddleware.Application.Services;
using ActindoMiddleware.Infrastructure.Actindo;
using ActindoMiddleware.Infrastructure.Actindo.Auth;
using ActindoMiddleware.Infrastructure.Configuration;
using ActindoMiddleware.Infrastructure.Nav;
using ActindoMiddleware.Infrastructure.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddControllers().AddJsonOptions(options =>
{
    options.JsonSerializerOptions.Converters.Add(
        new System.Text.Json.Serialization.JsonStringEnumConverter(
            System.Text.Json.JsonNamingPolicy.CamelCase));
});

builder.Services.Configure<ApiBehaviorOptions>(options =>
{
    var builtInFactory = options.InvalidModelStateResponseFactory;
    options.InvalidModelStateResponseFactory = context =>
    {
        var logger = context.HttpContext.RequestServices
            .GetRequiredService<ILoggerFactory>()
            .CreateLogger("ModelValidation");

        logger.LogWarning("Model validation failed for {Method} {Path}",
            context.HttpContext.Request.Method,
            context.HttpContext.Request.Path);

        foreach (var (key, entry) in context.ModelState)
        {
            foreach (var error in entry.Errors)
            {
                logger.LogWarning("  Field '{Field}': {Error}", key, error.ErrorMessage);
            }
        }

        return builtInFactory(context);
    };
});
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddOpenApi();

builder.Services.Configure<ActindoOAuthOptions>(
    builder.Configuration.GetSection("ActindoOAuth"));

builder.Services.AddSingleton<IUserStore, SqliteUserStore>();
builder.Services.AddSingleton<ISettingsStore, SqliteSettingsStore>();
builder.Services.AddSingleton<IActindoEndpointProvider, ActindoEndpointProvider>();
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.LoginPath = "/login";
        options.Cookie.Name = "actindo.middleware.auth";
        options.Cookie.HttpOnly = true;
        options.Cookie.SameSite = SameSiteMode.Lax;
        options.SlidingExpiration = true;
        options.ExpireTimeSpan = TimeSpan.FromDays(7);
        options.Events.OnRedirectToLogin = context =>
        {
            // API routes should return 401 instead of redirecting
            if (context.Request.Path.StartsWithSegments("/api"))
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            }

            context.Response.Redirect(context.RedirectUri);
            return Task.CompletedTask;
        };
        options.Events.OnRedirectToAccessDenied = context =>
        {
            context.Response.StatusCode = StatusCodes.Status403Forbidden;
            return Task.CompletedTask;
        };
    })
    .AddScheme<StaticBearerOptions, StaticBearerAuthenticationHandler>(
        StaticBearerDefaults.AuthenticationScheme,
        options =>
        {
            builder.Configuration.GetSection("StaticBearer").Bind(options);
        });

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy(AuthPolicies.Read, policy =>
        policy.AddAuthenticationSchemes(
                CookieAuthenticationDefaults.AuthenticationScheme,
                StaticBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireRole(UserRole.Read, UserRole.Write, UserRole.Admin));
    options.AddPolicy(AuthPolicies.Write, policy =>
        policy.AddAuthenticationSchemes(
                CookieAuthenticationDefaults.AuthenticationScheme,
                StaticBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireRole(UserRole.Write, UserRole.Admin));
    options.AddPolicy(AuthPolicies.Admin, policy =>
        policy.AddAuthenticationSchemes(
                CookieAuthenticationDefaults.AuthenticationScheme,
                StaticBearerDefaults.AuthenticationScheme)
            .RequireAuthenticatedUser()
            .RequireRole(UserRole.Admin));
});

builder.Services.AddSingleton<IActindoAvailabilityTracker, ActindoAvailabilityTracker>();
builder.Services.AddHttpClient<ActindoMiddleware.Infrastructure.Actindo.Auth.IAuthenticationService, ActindoMiddleware.Infrastructure.Actindo.Auth.AuthenticationService>();
builder.Services.AddHttpClient<ActindoClient>(client =>
{
    client.Timeout = TimeSpan.FromMinutes(30);
});
builder.Services.AddHttpClient<ActindoProductListService>();
builder.Services.AddHttpClient<INavClient, NavClient>();
builder.Services.AddHttpClient<ActindoMiddleware.Infrastructure.Nav.NavCallbackService>();
builder.Services.AddSingleton<ActindoMiddleware.Application.Services.ProductJobQueue>();
builder.Services.AddSingleton<IDashboardMetricsService, DashboardMetricsService>();
builder.Services.AddScoped<ProductCreateService>();
builder.Services.AddScoped<ProductSaveService>();
builder.Services.AddScoped<CustomerCreateService>();
builder.Services.AddScoped<CustomerSaveService>();
builder.Services.AddScoped<TransactionService>();
builder.Services.AddScoped<ProductImageService>();
builder.Services.AddHttpContextAccessor();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.MapOpenApi();
}

app.UseHttpsRedirection();

// Request logging middleware
app.Use(async (context, next) =>
{
    var logger = context.RequestServices.GetRequiredService<ILoggerFactory>()
        .CreateLogger("RequestLogging");
    logger.LogInformation(">>> {Method} {Path}{Query}",
        context.Request.Method,
        context.Request.Path,
        context.Request.QueryString);

    // Log request body for actindo API routes (for debugging)
    if (context.Request.Path.StartsWithSegments("/api/actindo") &&
        context.Request.ContentLength > 0 &&
        context.Request.ContentLength < 65536) // max 64KB to avoid huge logs
    {
        context.Request.EnableBuffering();
        var body = await new System.IO.StreamReader(context.Request.Body).ReadToEndAsync();
        context.Request.Body.Position = 0;
        logger.LogDebug("    Body: {Body}", body.Length > 2000 ? body[..2000] + "...(truncated)" : body);
    }

    await next();
    logger.LogInformation("<<< {Method} {Path} => {StatusCode}",
        context.Request.Method,
        context.Request.Path,
        context.Response.StatusCode);
});

app.UseAuthentication();
app.UseAuthorization();

app.UseDefaultFiles();
app.UseStaticFiles();

app.MapControllers();

// SPA Fallback: serve index.html for non-API, non-file routes
app.MapFallbackToFile("index.html");

app.Run();
