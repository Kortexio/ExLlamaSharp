using ExLlamaSharp.Engine;
using ExLlamaSharp.Server.Data;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ExLlamaSharp.Server.Services;

public sealed class EngineHostService : IHostedService, IAsyncDisposable
{
    private const int MaxRestarts = 3;

    private readonly ILogger<EngineHostService> _logger;
    private readonly SettingsService _settings;
    private readonly IConfiguration _configuration;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ArchitectureDetector _architectureDetector;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private IInferenceEngine? _engine;
    private string? _loadedModelPath;
    private Guid? _loadedModelId;
    private int _restartAttempts;
    private CancellationTokenSource? _watchdogCts;
    private Task? _watchdogTask;
    private bool _disposed;
    private readonly bool _forceMock;

    public EngineHostService(
        ILogger<EngineHostService> logger,
        SettingsService settings,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ArchitectureDetector architectureDetector)
    {
        _logger = logger;
        _settings = settings;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _architectureDetector = architectureDetector;
        _forceMock = configuration.GetValue("ExLlamaSharp:ForceMockEngine", false);
    }

    public IInferenceEngine Engine
    {
        get
        {
            EnsureEngine(null);
            return _engine!;
        }
    }

    public bool IsLoaded => _engine?.IsLoaded == true;
    public bool IsRunning => _engine?.IsRunning == true;
    public Guid? LoadedModelId => _loadedModelId;
    public string? LoadedModelPath => _loadedModelPath;

    /// <summary>
    /// True when the loaded EXL3 worker reported a working vision component.
    /// </summary>
    public bool SupportsVision => _engine?.SupportsVision == true;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        EnsureEngine(null);
        _watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _watchdogTask = Task.Run(() => WatchdogLoopAsync(_watchdogCts.Token), CancellationToken.None);

        try
        {
            var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            if (_forceMock || _engine!.IsMock)
            {
                await LoadAsync("mock://default", cancellationToken: cancellationToken).ConfigureAwait(false);
                _logger.LogInformation("Mock engine auto-loaded at mock://default");
                return;
            }

            if (!settings.LoadModelOnStartup)
            {
                _logger.LogInformation("LoadModelOnStartup is off — Admin will start without a GPU model");
                return;
            }

            if (settings.LastLoadedModelId is Guid modelId)
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var rec = await db.Models.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == modelId, cancellationToken)
                    .ConfigureAwait(false);
                if (rec is not null && Directory.Exists(rec.Path))
                {
                    _logger.LogInformation("Reloading last model {Alias} from {Path}", rec.Alias, rec.Path);
                    await LoadAsync(rec.Path, rec.Id, cancellationToken).ConfigureAwait(false);
                    return;
                }
            }

            var defaultPath = _configuration["ExLlamaSharp:DefaultModelPath"];
            if (!string.IsNullOrWhiteSpace(defaultPath) && Directory.Exists(defaultPath))
            {
                _logger.LogInformation("Loading DefaultModelPath {Path}", defaultPath);
                await LoadAsync(defaultPath, cancellationToken: cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Startup model load failed; Admin UI will stay up without a loaded model");
            try
            {
                await _settings.UpdateAsync(s => s.LastLoadedModelId = null, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception clearEx)
            {
                _logger.LogWarning(clearEx, "Could not clear LastLoadedModelId after a failed startup load");
            }
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        if (_watchdogCts is not null)
        {
            await _watchdogCts.CancelAsync().ConfigureAwait(false);
        }

        if (_watchdogTask is not null)
        {
            try
            {
                await _watchdogTask.WaitAsync(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // ignore
            }
        }

        await UnloadAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task LoadAsync(string modelPath, Guid? modelId = null, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            EnsureEngine(modelPath);

            if (_engine!.IsLoaded)
            {
                await UnloadAsync(cancellationToken).ConfigureAwait(false);
                EnsureEngine(modelPath);
            }

            var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                _ = new MultiGpuPlanner().BuildPlan(settings);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Multi-GPU settings invalid: {ex.Message}", ex);
            }

            var speculative = SpeculativeDecodingOptions.FromSettings(settings);
            if (speculative.Enabled)
            {
                speculative.ValidateOrThrow();
            }

            try
            {
                if (_engine is ExLlamaV3WorkerEngine worker)
                {
                    worker.Options = await WorkerOptionsFromSettingsAsync(cancellationToken).ConfigureAwait(false);
                }

                await _engine.LoadAsync(modelPath, cancellationToken).ConfigureAwait(false);
                _engine.Start();
            }
            catch
            {
                try
                {
                    await UnloadAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // keep the original load error
                }

                throw;
            }

            lock (_gate)
            {
                _loadedModelPath = modelPath;
                _loadedModelId = modelId;
                _restartAttempts = 0;
            }

            if (modelId is Guid id)
            {
                await _settings.UpdateAsync(s =>
                {
                    s.LastLoadedModelId = id;
                }, cancellationToken).ConfigureAwait(false);
            }

            _logger.LogInformation(
                "Loaded model from {Path} via {Engine} (IsMock={IsMock})",
                modelPath,
                _engine.GetType().Name,
                _engine.IsMock);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    /// <summary>Load by library id when A/B routes to a different model than the one currently loaded.</summary>
    public async Task EnsureModelIdLoadedAsync(Guid modelId, CancellationToken cancellationToken = default)
    {
        if (_loadedModelId == modelId && IsLoaded && IsRunning)
        {
            return;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var rec = await db.Models.AsNoTracking()
            .FirstOrDefaultAsync(m => m.Id == modelId, cancellationToken)
            .ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Model {modelId} not found in library.");

        if (!Directory.Exists(rec.Path) && !rec.Path.StartsWith("mock://", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"Model path missing: {rec.Path}");
        }

        await LoadAsync(rec.Path, rec.Id, cancellationToken).ConfigureAwait(false);
    }

    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        if (_engine is null)
        {
            return;
        }

        try
        {
            if (_engine.IsRunning)
            {
                _engine.Stop();
            }

            if (_engine.IsLoaded)
            {
                await _engine.UnloadAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Error while unloading engine");
        }
        finally
        {
            lock (_gate)
            {
                _loadedModelPath = null;
                _loadedModelId = null;
            }
        }
    }

    private void EnsureEngine(string? modelPath)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        lock (_gate)
        {
            var desired = SelectEngineKind(modelPath);
            if (_engine is not null && EngineMatches(_engine, desired))
            {
                return;
            }

            _engine?.Dispose();
            _engine = CreateEngine(desired);
            _logger.LogInformation("Using inference engine {Engine} (kind={Kind})", _engine.GetType().Name, desired);
        }
    }

    private enum EngineKind
    {
        Mock,
        Worker,
        Native,
    }

    private EngineKind SelectEngineKind(string? modelPath)
    {
        if (_forceMock)
        {
            return EngineKind.Mock;
        }

        if (!string.IsNullOrWhiteSpace(modelPath) &&
            modelPath.StartsWith("mock://", StringComparison.OrdinalIgnoreCase))
        {
            return EngineKind.Mock;
        }

        if (!string.IsNullOrWhiteSpace(modelPath) &&
            ExLlamaV3WorkerEngine.LooksLikeExl3Directory(modelPath) &&
            ExLlamaV3WorkerEngine.IsAvailable())
        {
            return EngineKind.Worker;
        }

        // Prefer worker whenever Python EXL3 stack is ready and no path yet
        // (so first EXL3 load does not stick to a broken native placeholder).
        if (string.IsNullOrWhiteSpace(modelPath) && ExLlamaV3WorkerEngine.IsAvailable())
        {
            return EngineKind.Worker;
        }

        return EngineKind.Native;
    }

    private static bool EngineMatches(IInferenceEngine engine, EngineKind kind) => kind switch
    {
        EngineKind.Mock => engine is MockEngine || (engine is ExLlamaEngine ex && ex.IsMock),
        EngineKind.Worker => engine is ExLlamaV3WorkerEngine,
        EngineKind.Native => engine is ExLlamaEngine { IsMock: false },
        _ => false,
    };

    private IInferenceEngine CreateEngine(EngineKind kind) => kind switch
    {
        EngineKind.Mock => ExLlamaEngine.Create(_logger, forceMock: true),
        EngineKind.Worker => new ExLlamaV3WorkerEngine(_logger, WorkerOptionsFromSettings()),
        _ => ExLlamaEngine.Create(_logger, forceMock: false),
    };

    private WorkerEngineOptions WorkerOptionsFromSettings() =>
        WorkerOptionsFromSettingsAsync(CancellationToken.None).GetAwaiter().GetResult();

    private async Task<WorkerEngineOptions> WorkerOptionsFromSettingsAsync(CancellationToken cancellationToken)
    {
        var s = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        string? draftPath = null;
        if (s.SpeculativeEnabled && s.DraftModelId is Guid draftId)
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var draft = await db.Models.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == draftId, cancellationToken)
                .ConfigureAwait(false);
            draftPath = draft?.Path;
            if (string.IsNullOrWhiteSpace(draftPath))
            {
                _logger.LogWarning("Speculative decoding enabled but draft model {Id} has no path", draftId);
            }
        }

        return new WorkerEngineOptions
        {
            MaxNumSeqs = Math.Max(1, s.MaxNumSeqs),
            MaxChunkSize = Math.Max(1, s.MaxChunkSize),
            MaxBatchedTokens = Math.Max(256, s.MaxBatchedTokens),
            CudaVisibleDevices = s.CudaVisibleDevices,
            ParallelismMode = s.ParallelismMode ?? "none",
            SpeculativeEnabled = s.SpeculativeEnabled,
            DraftModelPath = draftPath,
            DraftK = SpeculativeDecodingOptions.ClampDraftK(s.DraftK),
        };
    }

    private async Task WatchdogLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

                if (_engine is null || !_engine.IsLoaded)
                {
                    continue;
                }

                _ = _engine.GetMetrics();
                Interlocked.Exchange(ref _restartAttempts, 0);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Engine watchdog detected failure");
                await TryRestartAsync(cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private async Task TryRestartAsync(CancellationToken cancellationToken)
    {
        string? path;
        Guid? modelId;
        lock (_gate)
        {
            path = _loadedModelPath;
            modelId = _loadedModelId;
        }

        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        var attempt = Interlocked.Increment(ref _restartAttempts);
        if (attempt > MaxRestarts)
        {
            _logger.LogCritical("Engine restart limit ({Max}) exceeded; manual intervention required", MaxRestarts);
            return;
        }

        _logger.LogWarning("Restarting engine attempt {Attempt}/{Max}", attempt, MaxRestarts);

        try
        {
            await UnloadAsync(cancellationToken).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromSeconds(5), cancellationToken).ConfigureAwait(false);

            lock (_gate)
            {
                _engine?.Dispose();
                _engine = null;
            }

            await LoadAsync(path, modelId, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogError(ex, "Engine restart attempt {Attempt} failed", attempt);
        }
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _watchdogCts?.Cancel();
        _watchdogCts?.Dispose();

        if (_engine is not null)
        {
            await _engine.DisposeAsync().ConfigureAwait(false);
            _engine = null;
        }
    }
}
