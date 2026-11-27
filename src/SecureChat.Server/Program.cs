using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.IdentityModel.Tokens;
using SecureChat.Core.Contracts;
using SecureChat.Server.Data;
using SecureChat.Server.Hubs;
using SecureChat.Server.Services;

var builder = WebApplication.CreateBuilder(new WebApplicationOptions
{
    Args = args,
    WebRootPath = ResolveProjectWebRoot()
});

// Cлушаем на всех сетевых интерфейсах, чтобы клиенты из LAN (Android/LDPlayer) могли подключиться.
builder.WebHost.UseUrls("http://0.0.0.0:5080", "https://0.0.0.0:5443");

var jwtSettings = JwtSettings.Load(
    builder.Configuration,
    ServerDataPaths.ResolveDataDirectory(builder.Environment));

builder.Services.AddSingleton(jwtSettings);
builder.Services.AddSingleton<JwtTokenService>();
builder.Services.AddSingleton<PasswordHasher>();
builder.Services.AddSingleton<ChatRepository>();
builder.Services.AddSingleton<SpaceRepository>();
builder.Services.AddSingleton<StoryRepository>();
builder.Services.AddSingleton<EnvelopeRouter>();
builder.Services.AddSingleton<PresenceTracker>();
builder.Services.AddSingleton<PushNotificationService>();
builder.Services.AddHostedService<TcpChatServer>();

builder.Services.AddControllers();
builder.Services
    .AddSignalR(options =>
    {
        options.MaximumReceiveMessageSize = 128 * 1024;
        options.MaximumParallelInvocationsPerClient = 4;
        options.EnableDetailedErrors = builder.Environment.IsDevelopment();
    })
    .AddJsonProtocol(options =>
    {
        options.PayloadSerializerOptions.PropertyNamingPolicy = JsonNamingPolicy.CamelCase;
    });

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = jwtSettings.Issuer,
            ValidateAudience = true,
            ValidAudience = jwtSettings.Audience,
            ValidateIssuerSigningKey = true,
            IssuerSigningKey = new SymmetricSecurityKey(jwtSettings.KeyBytes),
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(2),
            NameClaimType = ClaimTypes.Name
        };

    });

builder.Services.AddAuthorization();

builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;

    // Общий лимит на все эндпоинты: 600 запросов в минуту с одного IP.
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 600,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));

    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            partitionKey: httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            factory: _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 10,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

// HTTPS для LAN: сертификат с адресами сервера в SAN (App_Data/lan-tls.pfx).
// Без него Kestrel отдаёт dev-сертификат localhost, и клиенты по IP его отклоняют.
var lanCertificate = LanTlsCertificate.TryLoad(ServerDataPaths.ResolveDataDirectory(builder.Environment));

// Kestrel по умолчанию режет тело запроса на 30 МБ: поднимаем под лимит медиа
// (плюс небольшой запас под обрамление HTTP-запроса).
builder.WebHost.ConfigureKestrel(options =>
{
    options.Limits.MaxRequestBodySize = MediaLimits.MaxBytes + 64L * 1024;
    if (lanCertificate is not null)
    {
        options.ConfigureHttpsDefaults(https => https.ServerCertificate = lanCertificate);
    }
});

// Статические файлы (редактор БД) ищем в wwwroot каталога проекта — при запуске
// exe из bin\Release рабочий каталог другой, и файл не находился бы.
static string ResolveProjectWebRoot()
{
    var current = Directory.GetCurrentDirectory();
    while (!string.IsNullOrEmpty(current) &&
           !File.Exists(Path.Combine(current, "SecureChat.Server.csproj")))
    {
        current = Directory.GetParent(current)?.FullName ?? string.Empty;
    }

    return string.IsNullOrEmpty(current)
        ? Path.Combine(Directory.GetCurrentDirectory(), "wwwroot")
        : Path.Combine(current, "wwwroot");
}

var app = builder.Build();

app.UseExceptionHandler(errorApp =>
{
    errorApp.Run(async context =>
    {
        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
        context.Response.ContentType = "application/json; charset=utf-8";
        await context.Response.WriteAsJsonAsync(new ApiError
        {
            Error = "Внутренняя ошибка сервера."
        });
    });
});

app.Use(async (context, next) =>
{
    context.Response.Headers["X-Content-Type-Options"] = "nosniff";
    context.Response.Headers["X-Frame-Options"] = "DENY";
    context.Response.Headers["Referrer-Policy"] = "no-referrer";
    await next();
});

app.UseRateLimiter();

// Статические файлы (в том числе админский редактор БД /admin/db.html).
app.UseStaticFiles();

app.UseAuthentication();
app.UseAuthorization();

app.MapGet("/api/health", () => Results.Ok(new
{
    status = "ok",
    serverTime = DateTimeOffset.UtcNow
}));

app.MapControllers();
app.MapHub<ChatHub>("/hubs/chat");

await app.Services.GetRequiredService<ChatRepository>().InitializeAsync();
await app.Services.GetRequiredService<SpaceRepository>().InitializeAsync();
await app.Services.GetRequiredService<StoryRepository>().InitializeAsync();

app.Logger.LogInformation(
    "SecureChat server запущен. JWT issuer={Issuer}, audience={Audience}",
    jwtSettings.Issuer,
    jwtSettings.Audience);

app.Logger.LogInformation(
    "HTTPS-сертификат: {Source}",
    lanCertificate is null
        ? "dev-сертификат localhost (для LAN выполните tools/New-LanTlsCertificate.ps1)"
        : $"LAN-сертификат ({lanCertificate.Subject}, до {lanCertificate.NotAfter:yyyy-MM-dd})");

// Выводим все доступные сетевые адреса для подключения клиентов (LAN, Wi-Fi, Ethernet и т.д.)
try
{
    var localAddresses = new List<string> { "127.0.0.1" };
    foreach (var iface in NetworkInterface.GetAllNetworkInterfaces())
    {
        if (iface.OperationalStatus != OperationalStatus.Up)
            continue;
        if (iface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            continue;

        foreach (var addr in iface.GetIPProperties().UnicastAddresses)
        {
            if (addr.Address.AddressFamily != AddressFamily.InterNetwork)
                continue;
            if (IPAddress.IsLoopback(addr.Address))
                continue;

            localAddresses.Add(addr.Address.ToString());
        }
    }

    Console.WriteLine("Доступные адреса сервера:");
    foreach (var ip in localAddresses.Distinct())
    {
        Console.WriteLine($"  HTTP:  http://{ip}:5080");
        Console.WriteLine($"  HTTPS: https://{ip}:5443");
        Console.WriteLine($"  TCP:   tcp://{ip}:5444");
    }
}
catch (Exception ex)
{
    app.Logger.LogWarning(ex, "Не удалось получить список сетевых адресов");
}

app.Run();

public partial class Program;
