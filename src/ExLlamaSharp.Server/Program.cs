using System.Runtime;
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

builder.Services.AddExLlamaSharpUi();

builder.Logging.AddConsole();
builder.Logging.AddDebug();
builder.Services.AddSingleton<ILoggerProvider, LiveLogLoggerProvider>();

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen(c =>
{
    c.SwaggerDoc("v1", new() { Title = "ExLlamaSharp API", Version = "v1" });
});

builder.Services.AddCors(options =>
{
    options.AddDefaultPolicy(policy =>
        policy.AllowAnyHeader().AllowAnyMethod().AllowAnyOrigin());
});

builder.WebHost.ConfigureKestrel((context, options) =>
{
    var port = context.Configuration.GetValue("Kestrel:Port", 14563);
    var bind = context.Configuration.GetValue("Kestrel:Bind", "0.0.0.0") ?? "0.0.0.0";
    TryApplyListenSettingsFromDatabase(dataRoot, ref bind, ref port);
    if (bind is "0.0.0.0" or "*")
    {
        options.ListenAnyIP(port);
    }
    else if (bind is "127.0.0.1" or "localhost")
    {
        options.ListenLocalhost(port);
    }
    else if (System.Net.IPAddress.TryParse(bind, out var ip))
    {
        options.Listen(ip, port);
    }
    else
    {
        options.ListenAnyIP(port);
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

app.Logger.LogInformation(
    "ExLlamaSharp listening. GC LatencyMode={Mode}, DataRoot={DataRoot}",
    GCSettings.LatencyMode,
    dataRoot);

await app.RunAsync();

static void TryApplyListenSettingsFromDatabase(string root, ref string bind, ref int port)
{
    try
    {
        var dbPath = Path.Combine(root, "app.db");
        if (!File.Exists(dbPath))
        {
            return;
        }

        using var conn = new Microsoft.Data.Sqlite.SqliteConnection($"Data Source={dbPath}");
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT BindAddress, Port FROM Settings LIMIT 1";
        using var reader = cmd.ExecuteReader();
        if (!reader.Read())
        {
            return;
        }

        if (!reader.IsDBNull(0))
        {
            var dbBind = reader.GetString(0);
            if (!string.IsNullOrWhiteSpace(dbBind))
            {
                bind = dbBind.Trim();
            }
        }

        if (!reader.IsDBNull(1))
        {
            var dbPort = reader.GetInt32(1);
            if (dbPort is > 0 and < 65536)
            {
                port = dbPort;
            }
        }
    }
    catch
    {
        // first run / schema not ready — keep appsettings defaults
    }
}

public partial class Program;
