using System.Runtime;
using System.Security.Cryptography.X509Certificates;
using ExLlamaSharp.Performance;
using ExLlamaSharp.Server;
using ExLlamaSharp.Server.Data;
using ExLlamaSharp.Server.Services;
using Microsoft.EntityFrameworkCore;

// .NET 10 performance: sustained low latency for /v1 hot path
GcTuning.EnableSustainedLowLatency();

// Windows Service cwd is System32; always pin content root to the install folder.
var options = new WebApplicationOptions
{
    Args = args,
    ContentRootPath = AppContext.BaseDirectory
};
var builder = WebApplication.CreateBuilder(options);

builder.Host.UseWindowsService(options =>
{
    options.ServiceName = "ExLlamaSharp";
});

// Port bind / shutdown races must not take down the process via a background timer cancel.
builder.Services.Configure<HostOptions>(o =>
{
    o.BackgroundServiceExceptionBehavior = BackgroundServiceExceptionBehavior.Ignore;
});

var dataRoot = Environment.GetEnvironmentVariable("EXLLAMASHARP_DATA_ROOT");
if (string.IsNullOrWhiteSpace(dataRoot))
{
    dataRoot = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "ExLlamaSharp");
}

Directory.CreateDirectory(dataRoot);
Directory.CreateDirectory(Path.Combine(dataRoot, "logs"));
Directory.CreateDirectory(Path.Combine(dataRoot, "models"));
Directory.CreateDirectory(Path.Combine(dataRoot, "backups"));

var dbPath = Path.Combine(dataRoot, "app.db");
builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseSqlite($"Data Source={dbPath}"));

builder.Services.AddMemoryCache();
builder.Services.AddHttpClient();

builder.Services.AddSingleton<LiveLogBuffer>();
builder.Services.AddSingleton<KeyCacheService>();
builder.Services.AddSingleton<RateLimiter>();
builder.Services.AddSingleton<SettingsService>();
builder.Services.AddSingleton<AbTestRouter>();
builder.Services.AddSingleton<TenantResolver>();
builder.Services.AddSingleton<ContentModerationService>();
builder.Services.AddSingleton<MetricsHistoryService>();
builder.Services.AddSingleton<EmbeddingService>();
builder.Services.AddSingleton<AboutService>();
builder.Services.AddSingleton<HealthService>();
builder.Services.AddSingleton<WebhookService>();
builder.Services.AddSingleton<ModelJobsService>();
builder.Services.AddSingleton<BackupService>();
builder.Services.AddSingleton<MultiGpuPlanner>();
builder.Services.AddSingleton<ArchitectureDetector>();
builder.Services.AddSingleton<LoraAdapterService>();
builder.Services.AddSingleton<PythonModelTools>();
builder.Services.AddSingleton<EngineHostService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<EngineHostService>());
builder.Services.AddSingleton<AuditService>();
builder.Services.AddHostedService(sp => sp.GetRequiredService<AuditService>());
builder.Services.AddHostedService(sp => sp.GetRequiredService<BackupService>());
builder.Services.AddHostedService<DashboardBroadcastService>();

builder.Services.AddExLlamaSharpUi();

builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Services.AddSingleton<ILoggerProvider, LiveLogLoggerProvider>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "ExLlamaSharp API", Version = "v1" });
});

var dbListen = ReadListenSettingsFromDatabase(dataRoot);
var corsValue = string.IsNullOrWhiteSpace(dbListen.Cors) ? "*" : dbListen.Cors.Trim();

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
    {
        policy.AllowAnyHeader().AllowAnyMethod();
        if (corsValue is "*" or "")
        {
            policy.AllowAnyOrigin();
        }
        else
        {
            var origins = corsValue
                .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            if (origins.Length == 0)
            {
                policy.AllowAnyOrigin();
            }
            else
            {
                policy.WithOrigins(origins);
            }
        }
    });
});

X509Certificate2? tlsCert = null;
var tlsActive = false;
if (!string.IsNullOrWhiteSpace(dbListen.TlsCertPath))
{
    tlsCert = TryLoadTlsCertificate(dbListen.TlsCertPath);
    tlsActive = tlsCert is not null;
}

builder.WebHost.ConfigureKestrel((context, options) =>
{
    var port = context.Configuration.GetValue("Kestrel:Port", 14563);
    var bind = context.Configuration.GetValue("Kestrel:Bind", "0.0.0.0") ?? "0.0.0.0";
    if (!string.IsNullOrWhiteSpace(dbListen.Bind))
    {
        bind = dbListen.Bind;
    }

    if (dbListen.Port is > 0 and < 65536)
    {
        port = dbListen.Port.Value;
    }

    void ConfigureListen(Microsoft.AspNetCore.Server.Kestrel.Core.ListenOptions listenOptions)
    {
        if (tlsCert is not null)
        {
            listenOptions.UseHttps(tlsCert);
        }
    }

    if (bind is "0.0.0.0" or "*")
    {
        options.ListenAnyIP(port, ConfigureListen);
    }
    else if (bind is "127.0.0.1" or "localhost")
    {
        options.ListenLocalhost(port, ConfigureListen);
    }
    else if (System.Net.IPAddress.TryParse(bind, out var ip))
    {
        options.Listen(ip, port, ConfigureListen);
    }
    else
    {
        options.ListenAnyIP(port, ConfigureListen);
    }

    options.Limits.MaxConcurrentConnections = 1000;
    options.Limits.Http2.MaxStreamsPerConnection = 100;
});

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var logger = scope.ServiceProvider.GetRequiredService<ILoggerFactory>().CreateLogger("DbInitializer");
    await DbInitializer.InitializeAsync(db, logger);
    var inventory = scope.ServiceProvider.GetRequiredService<ModelInventoryService>();
    await inventory.SyncFromDiskAsync();
}

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.UseStaticFiles();
app.UseCors();
app.UseExLlamaSharpApi();
app.MapExLlamaSharpEndpoints();
app.MapExLlamaSharpUi();

if (tlsActive)
{
    app.Logger.LogInformation("TLS active using certificate from {Path}", dbListen.TlsCertPath);
}
else
{
    app.Logger.LogInformation(
        "TLS inactive{Reason}",
        string.IsNullOrWhiteSpace(dbListen.TlsCertPath)
            ? " (TlsCertPath not set)."
            : $" (could not load certificate from '{dbListen.TlsCertPath}').");
}

app.Logger.LogInformation(
    "ExLlamaSharp listening. GC LatencyMode={Mode}, DataRoot={DataRoot}, Cors={Cors}",
    GCSettings.LatencyMode,
    dataRoot,
    corsValue);

await app.RunAsync();

static DbListenSettings ReadListenSettingsFromDatabase(string root)
{
    var result = new DbListenSettings();
    try
    {
        var dbPath = Path.Combine(root, "app.db");
        if (!File.Exists(dbPath))
        {
            return result;
        }

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT BindAddress, Port, Cors, TlsCertPath FROM Settings LIMIT 1";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return result;
        }

        if (!reader.IsDBNull(0))
        {
            var dbBind = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(dbBind))
            {
                result.Bind = dbBind.Trim();
            }
        }

        if (!reader.IsDBNull(1))
        {
            var dbPort = reader.GetInt32(1);
            if (dbPort is > 0 and < 65536)
            {
                result.Port = dbPort;
            }
        }

        if (reader.FieldCount > 2 && !reader.IsDBNull(2))
        {
            result.Cors = reader.GetString(2);
        }

        if (reader.FieldCount > 3 && !reader.IsDBNull(3))
        {
            result.TlsCertPath = reader.GetString(3);
        }
    }
    catch
    {
        // first run / schema not ready — keep appsettings defaults
    }

    return result;
}

static X509Certificate2? TryLoadTlsCertificate(string path)
{
    try
    {
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            return null;
        }

        var password = Environment.GetEnvironmentVariable("EXLLAMASHARP_TLS_CERT_PASSWORD");
        var ext = Path.GetExtension(path);

        if (ext.Equals(".pfx", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".p12", StringComparison.OrdinalIgnoreCase))
        {
#pragma warning disable SYSLIB0057 // X509Certificate2(string,string) obsolete on newer TFMs; fine for net10 LoadPkcs12
            return new X509Certificate2(path, password ?? string.Empty);
#pragma warning restore SYSLIB0057
        }

        if (ext.Equals(".pem", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".crt", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".cer", StringComparison.OrdinalIgnoreCase))
        {
            var keyPath = FindPemKeyPath(path);
            if (keyPath is null || !File.Exists(keyPath))
            {
                return null;
            }

            var cert = X509Certificate2.CreateFromPemFile(path, keyPath);
            // Windows Kestrel needs an exportable PKCS12-backed cert for ephemeral keys.
#pragma warning disable SYSLIB0057
            return new X509Certificate2(cert.Export(X509ContentType.Pkcs12));
#pragma warning restore SYSLIB0057
        }
    }
    catch
    {
        return null;
    }

    return null;
}

static string? FindPemKeyPath(string certPath)
{
    var dir = Path.GetDirectoryName(certPath) ?? ".";
    var baseName = Path.GetFileNameWithoutExtension(certPath);
    string[] candidates =
    [
        Path.Combine(dir, baseName + ".key"),
        Path.Combine(dir, baseName + "-key.pem"),
        Path.Combine(dir, baseName + ".key.pem"),
        Path.Combine(dir, "key.pem"),
        Path.Combine(dir, "privkey.pem"),
    ];

    foreach (var c in candidates)
    {
        if (File.Exists(c))
        {
            return c;
        }
    }

    return null;
}

file sealed class DbListenSettings
{
    public string? Bind { get; set; }
    public int? Port { get; set; }
    public string? Cors { get; set; }
    public string? TlsCertPath { get; set; }
}

public partial class Program;
