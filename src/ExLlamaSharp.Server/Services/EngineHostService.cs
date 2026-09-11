using ExLlamaSharp.Engine;
using ExLlamaSharp.Engine.Worker;
using ExLlamaSharp.Server.Data;
using ExLlamaSharp.Server.Data.Entities;
using ExLlamaSharp.Server.Services.Ui;
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
    private readonly IHostEnvironment _environment;
    private readonly object _gate = new();
    private readonly SemaphoreSlim _loadLock = new(1, 1);
    private IInferenceEngine? _engine;
    private string? _loadedModelPath;
    private Guid? _loadedModelId;
    private int _restartAttempts;
    private int _loadingFlag;
    private string? _lastLoadError;
    private Guid? _loadingModelId;
    private CancellationTokenSource? _loadCts;
    private CancellationTokenSource? _watchdogCts;
    private Task? _watchdogTask;
    private Task? _loadTask;
    private bool _disposed;
    private readonly bool _forceMock;
    private string? _loadPhase;
    private int _loadProgressPct;
    private DateTime? _loadStartedUtc;

    public EngineHostService(
        ILogger<EngineHostService> logger,
        SettingsService settings,
        IConfiguration configuration,
        IServiceScopeFactory scopeFactory,
        ArchitectureDetector architectureDetector,
        IHostEnvironment environment)
    {
        _logger = logger;
        _settings = settings;
        _configuration = configuration;
        _scopeFactory = scopeFactory;
        _architectureDetector = architectureDetector;
        _environment = environment;
        var requestedMock = configuration.GetValue("ExLlamaSharp:ForceMockEngine", false);
        _forceMock = requestedMock && environment.IsDevelopment();
        if (requestedMock && !environment.IsDevelopment())
        {
            _logger.LogError("ForceMockEngine is ignored outside Development");
        }
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
    public bool IsLoading => Volatile.Read(ref _loadingFlag) == 1;
    public string? LastLoadError => _lastLoadError;
    public Guid? LoadingModelId => _loadingModelId;
    public Guid? LoadedModelId => _loadedModelId;
    public string? LoadedModelPath => _loadedModelPath;
    public string? LoadPhase => _loadPhase;
    public int LoadProgressPct => _loadProgressPct;
    public long? LoadElapsedMs => _loadStartedUtc is DateTime started
        ? (long)(DateTime.UtcNow - started).TotalMilliseconds
        : null;

    /// <summary>
    /// True when the loaded EXL3 worker reported a working vision component.
    /// </summary>
    public bool SupportsVision => _engine?.SupportsVision == true;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        try
        {
            EnsureEngine(null);
        }
        catch (Exception ex)
        {
            _lastLoadError = ex.Message;
            _logger.LogError(ex, "EXL3 worker unavailable at start — Admin will stay up");
        }

        _watchdogCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _watchdogTask = Task.Run(() => WatchdogLoopAsync(_watchdogCts.Token), CancellationToken.None);

        try
        {
            var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            if (_forceMock)
            {
                await LoadAsync("mock://default", cancellationToken: cancellationToken).ConfigureAwait(false);
                _logger.LogWarning("Development mock engine auto-loaded");
                return;
            }

            if (ProductionRuntime.IsSessionZero() && !ProductionRuntime.IsHeadless)
            {
                _lastLoadError =
                    "GPU load refused in Session 0 (Windows service / LocalSystem). Use the Tray so the Server runs in the user session.";
                _logger.LogError("{Error}", _lastLoadError);
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
                    _logger.LogInformation("Queueing last model {Alias} from {Path}", rec.Alias, rec.Path);
                    if (!TryQueueLoad(rec.Path, rec.Id, out var reject))
                    {
                        _lastLoadError = reject;
                        _logger.LogWarning("Startup model queue rejected: {Reason}", reject);
                    }

                    return;
                }
            }

            var defaultPath = _configuration["ExLlamaSharp:DefaultModelPath"];
            if (!string.IsNullOrWhiteSpace(defaultPath) && Directory.Exists(defaultPath))
            {
                _logger.LogInformation("Queueing DefaultModelPath {Path}", defaultPath);
                if (!TryQueueLoad(defaultPath, null, out var reject))
                {
                    _lastLoadError = reject;
                    _logger.LogWarning("Startup DefaultModelPath queue rejected: {Reason}", reject);
                }
            }
        }
        catch (Exception ex)
        {
            _lastLoadError = ex.Message;
            _logger.LogError(ex, "Startup model queue failed; Admin UI will stay up without a loaded model");
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

    public async Task LoadAsync(
        string modelPath,
        Guid? modelId = null,
        CancellationToken cancellationToken = default,
        string? loadProfile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (!_forceMock
            && !modelPath.StartsWith("mock://", StringComparison.OrdinalIgnoreCase)
            && ProductionRuntime.IsSessionZero()
            && !ProductionRuntime.IsHeadless)
        {
            throw new InvalidOperationException(
                "GPU load refused in Session 0 (Windows service / LocalSystem). Use the Tray (desktop) or a GPU-capable service account (headless).");
        }

        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (loadProfile is not null)
            {
                await ApplyLoadProfileAsync(loadProfile, modelPath, modelId, cancellationToken).ConfigureAwait(false);
            }

            EnsureEngine(modelPath);

            if (_engine!.IsLoaded)
            {
                await UnloadCoreAsync(cancellationToken).ConfigureAwait(false);
                EnsureEngine(modelPath);
            }

            var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
            MultiGpuPlan plan;
            try
            {
                plan = new MultiGpuPlanner().BuildPlan(settings);
            }
            catch (Exception ex)
            {
                throw new InvalidOperationException($"Multi-GPU settings invalid: {ex.Message}", ex);
            }

            if (!string.IsNullOrWhiteSpace(plan.CoercionNote))
            {
                _logger.LogWarning("{Note}", plan.CoercionNote);
                try
                {
                    await _settings.UpdateAsync(s => s.ParallelismMode = plan.AppliedMode, cancellationToken)
                        .ConfigureAwait(false);
                    settings.ParallelismMode = plan.AppliedMode;
                }
                catch (Exception persistEx)
                {
                    _logger.LogWarning(persistEx, "Could not persist coerced ParallelismMode={Mode}", plan.AppliedMode);
                }
            }

            await EnsureVramFitsAsync(settings, plan, modelPath, modelId, cancellationToken).ConfigureAwait(false);

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
                    worker.LoadProgress += OnWorkerLoadProgress;
                }

                SetLoadProgress("starting", 1);
                await _engine.LoadAsync(modelPath, cancellationToken).ConfigureAwait(false);
                _engine.Start();
                SetLoadProgress("ready", 100);
            }
            catch
            {
                try
                {
                    await UnloadCoreAsync(CancellationToken.None).ConfigureAwait(false);
                }
                catch
                {
                    // keep the original load error
                }

                throw;
            }
            finally
            {
                if (_engine is ExLlamaV3WorkerEngine done)
                {
                    done.LoadProgress -= OnWorkerLoadProgress;
                }
            }

            lock (_gate)
            {
                _loadedModelPath = modelPath;
                _loadedModelId = modelId;
                _restartAttempts = 0;
            }

            if (modelId is Guid id)
            {
                try
                {
                    await _settings.UpdateAsync(s =>
                    {
                        s.LastLoadedModelId = id;
                        s.LoadModelOnStartup = true;
                    }, cancellationToken).ConfigureAwait(false);
                }
                catch (Exception ex)
                {
                    // Model is already in VRAM — do not unload just because persistence failed.
                    _logger.LogWarning(ex, "Model loaded but failed to persist LastLoadedModelId");
                }
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

    /// <summary>
    /// Queue a model load on a background task so Blazor circuits / HTTP callers are not blocked for minutes.
    /// </summary>
    public bool TryQueueLoad(string modelPath, Guid? modelId, out string? rejectReason, string? loadProfile = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);
        if (Interlocked.CompareExchange(ref _loadingFlag, 1, 0) != 0)
        {
            rejectReason = "A model load is already in progress.";
            return false;
        }

        _lastLoadError = null;
        _loadingModelId = modelId;
        SetLoadProgress("queued", 0);
        rejectReason = null;

        var previous = Interlocked.Exchange(ref _loadCts, new CancellationTokenSource(TimeSpan.FromSeconds(90)));
        try { previous?.Cancel(); } catch { /* ignore */ }
        previous?.Dispose();
        var cts = _loadCts;

        _loadTask = Task.Run(async () =>
        {
            try
            {
                await LoadAsync(modelPath, modelId, cts.Token, loadProfile).ConfigureAwait(false);
                _lastLoadError = null;
            }
            catch (OperationCanceledException)
            {
                _lastLoadError = cts.IsCancellationRequested && !cts.Token.CanBeCanceled
                    ? "Model load cancelled."
                    : "Model load timed out after 90 seconds. Session 0/LocalSystem CUDA often hangs — use the Tray.";
                _logger.LogError("Background model load cancelled/timed out for {Path}", modelPath);
                await ResetEngineAfterFailedLoadAsync().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _lastLoadError = ex.Message;
                _logger.LogError(ex, "Background model load failed for {Path}", modelPath);
                await ResetEngineAfterFailedLoadAsync().ConfigureAwait(false);
            }
            finally
            {
                Interlocked.Exchange(ref _loadingFlag, 0);
            }
        });

        return true;
    }

    /// <summary>Cancel a stuck background load and reset the worker.</summary>
    public async Task CancelLoadAsync(CancellationToken cancellationToken = default)
    {
        _lastLoadError = "Load cancelled.";
        try { _loadCts?.Cancel(); } catch { /* ignore */ }
        var loadTask = _loadTask;
        if (loadTask is not null)
        {
            try
            {
                await loadTask.WaitAsync(TimeSpan.FromSeconds(15), cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // load may still hold the lock; reset below
            }
        }

        await ResetEngineAfterFailedLoadAsync().ConfigureAwait(false);
        Interlocked.Exchange(ref _loadingFlag, 0);
    }

    private async Task ResetEngineAfterFailedLoadAsync()
    {
        try
        {
            if (_loadLock.Wait(0))
            {
                try
                {
                    await UnloadCoreAsync(CancellationToken.None).ConfigureAwait(false);
                    lock (_gate)
                    {
                        _engine?.Dispose();
                        _engine = null;
                    }
                }
                finally
                {
                    _loadLock.Release();
                }
            }
            else
            {
                // Load holds the lock — dispose underneath as last resort.
                lock (_gate)
                {
                    try { _engine?.Dispose(); } catch { /* ignore */ }
                    _engine = null;
                    _loadedModelPath = null;
                    _loadedModelId = null;
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Engine reset after failed load");
        }

        await Task.CompletedTask.ConfigureAwait(false);
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

    public static bool GpuRuntimeSettingsChanged(AppSettings before, AppSettings after)
    {
        ArgumentNullException.ThrowIfNull(before);
        ArgumentNullException.ThrowIfNull(after);
        return !string.Equals(before.CudaVisibleDevices, after.CudaVisibleDevices, StringComparison.Ordinal)
            || !string.Equals(before.ParallelismMode, after.ParallelismMode, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(before.GpuSplitGb ?? "", after.GpuSplitGb ?? "", StringComparison.Ordinal)
            || Math.Abs(before.GpuMemoryUtilization - after.GpuMemoryUtilization) > 1e-9;
    }

    /// <summary>Kill the Python worker (CVD is process-env) and reload the current model if any.</summary>
    public async Task RecycleWorkerForGpuSettingsAsync(CancellationToken cancellationToken = default)
    {
        var path = LoadedModelPath;
        var id = LoadedModelId;
        await UnloadAsync(cancellationToken).ConfigureAwait(false);
        lock (_gate)
        {
            _engine?.Dispose();
            _engine = null;
        }

        Interlocked.Exchange(ref _loadingFlag, 0);

        if (!string.IsNullOrWhiteSpace(path) && !TryQueueLoad(path, id, out var reject))
        {
            _logger.LogWarning("GPU settings applied but reload was rejected: {Reason}", reject);
        }
    }

    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        await _loadLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await UnloadCoreAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _loadLock.Release();
        }
    }

    private async Task UnloadCoreAsync(CancellationToken cancellationToken)
    {
        if (_engine is null)
        {
            lock (_gate)
            {
                _loadedModelPath = null;
                _loadedModelId = null;
            }

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
        if (_environment.IsDevelopment()
            && (_forceMock
                || (!string.IsNullOrWhiteSpace(modelPath)
                    && modelPath.StartsWith("mock://", StringComparison.OrdinalIgnoreCase))))
        {
            return EngineKind.Mock;
        }

        if (!ExLlamaV3WorkerEngine.IsAvailable())
        {
            throw new InvalidOperationException(
                "EXL3 Python worker is not available. Install the venv (Setup-Exl3Python) and set exl3-runtime.json. Mock/native stub is disabled.");
        }

        return EngineKind.Worker;
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
        _ => throw new InvalidOperationException("Native stub engine is disabled. Use the EXL3 Python worker."),
    };

    private async Task EnsureVramFitsAsync(
        AppSettings settings,
        MultiGpuPlan plan,
        string modelPath,
        Guid? modelId,
        CancellationToken cancellationToken)
    {
        var weightGb = await ResolveWeightGbAsync(modelPath, modelId, cancellationToken).ConfigureAwait(false);
        if (weightGb <= 0)
        {
            return;
        }

        var gpus = CudaDeviceEnvironment.QueryGpus()
            .Select(g => new GpuSnapshot
            {
                Index = g.Index,
                Name = g.Name,
                MemoryTotalMb = g.MemoryTotalMiB,
                MemoryUsedMb = g.MemoryUsedMiB,
                Uuid = g.Uuid,
            })
            .ToList();
        var visible = GpuInfoService.FilterVisible(gpus, settings.CudaVisibleDevices);
        var fit = new VramFitService();
        if (fit.TryExplainLoadRefusal(
                weightGb,
                visible,
                settings.GpuMemoryUtilization,
                settings.GpuSplitGb,
                plan.MaxBatchedTokens,
                out var error))
        {
            throw new InvalidOperationException(error);
        }
    }

    private async Task ApplyLoadProfileAsync(
        string loadProfile,
        string modelPath,
        Guid? modelId,
        CancellationToken cancellationToken)
    {
        var profileKey = VramFitService.NormalizeLoadProfile(loadProfile);
        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        var weightGb = await ResolveWeightGbAsync(modelPath, modelId, cancellationToken).ConfigureAwait(false);
        var gpus = CudaDeviceEnvironment.QueryGpus()
            .Select(g => new GpuSnapshot
            {
                Index = g.Index,
                Name = g.Name,
                MemoryTotalMb = g.MemoryTotalMiB,
                MemoryUsedMb = g.MemoryUsedMiB,
                Uuid = g.Uuid,
            })
            .ToList();
        var fit = new VramFitService();
        var profiles = fit.BuildLoadProfiles(
            weightGb,
            gpus,
            settings.GpuMemoryUtilization,
            settings.GpuSplitGb,
            settings.CudaVisibleDevices,
            settings.MaxBatchedTokens,
            settings.ParallelismMode);

        if (profileKey == VramFitService.ProfileCustom)
        {
            var plan = new MultiGpuPlanner().BuildPlan(settings);
            if (!string.IsNullOrWhiteSpace(plan.CoercionNote))
            {
                await _settings.UpdateAsync(s => s.ParallelismMode = plan.AppliedMode, cancellationToken)
                    .ConfigureAwait(false);
                _logger.LogWarning("{Note}", plan.CoercionNote);
            }
        }
        else
        {
            var chosen = fit.GetProfile(profiles, profileKey);
            if (chosen is null || !chosen.Available)
            {
                var detail = chosen?.Summary
                    ?? profiles.Profiles.FirstOrDefault()?.Summary
                    ?? "Model does not fit the visible GPU split.";
                throw new InvalidOperationException(detail);
            }

            await _settings.UpdateAsync(s =>
            {
                s.MaxBatchedTokens = chosen.MaxBatchedTokens;
                s.ParallelismMode = chosen.ParallelismMode;
                if (string.IsNullOrWhiteSpace(s.GpuSplitGb) && !string.IsNullOrWhiteSpace(chosen.GpuSplitGb))
                {
                    s.GpuSplitGb = chosen.GpuSplitGb;
                }
            }, cancellationToken).ConfigureAwait(false);

            _logger.LogInformation(
                "Applied load profile {Profile}: max_batched_tokens={Tokens} parallelism={Mode}",
                profileKey,
                chosen.MaxBatchedTokens,
                chosen.ParallelismMode);
        }

        if (modelId is Guid persistId)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var record = await db.Models.FirstOrDefaultAsync(m => m.Id == persistId, cancellationToken)
                    .ConfigureAwait(false);
                if (record is not null)
                {
                    record.LastLoadProfile = profileKey;
                    await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Could not persist LastLoadProfile={Profile}", profileKey);
            }
        }
    }

    private async Task<double> ResolveWeightGbAsync(
        string modelPath,
        Guid? modelId,
        CancellationToken cancellationToken)
    {
        var weightGb = 0d;
        if (modelId is Guid id)
        {
            try
            {
                await using var scope = _scopeFactory.CreateAsyncScope();
                var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                var rec = await db.Models.AsNoTracking()
                    .FirstOrDefaultAsync(m => m.Id == id, cancellationToken)
                    .ConfigureAwait(false);
                if (rec is not null && rec.SizeGb > 0)
                {
                    weightGb = rec.SizeGb;
                }
            }
            catch
            {
                // fall through to folder measure
            }
        }

        if (weightGb <= 0
            && !string.IsNullOrWhiteSpace(modelPath)
            && !modelPath.StartsWith("mock://", StringComparison.OrdinalIgnoreCase)
            && Directory.Exists(modelPath))
        {
            try
            {
                long bytes = 0;
                foreach (var file in Directory.EnumerateFiles(modelPath, "*", SearchOption.AllDirectories))
                {
                    bytes += new FileInfo(file).Length;
                }

                weightGb = bytes / (1024d * 1024d * 1024d);
            }
            catch
            {
                // skip
            }
        }

        return weightGb;
    }

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

        var plan = new MultiGpuPlanner().BuildPlan(s);
        if (!string.IsNullOrWhiteSpace(plan.CoercionNote))
        {
            _logger.LogWarning("{Note}", plan.CoercionNote);
        }

        var cuda = CudaDeviceEnvironment.Normalize(s.CudaVisibleDevices, out var cudaWarning);
        if (!string.IsNullOrWhiteSpace(cudaWarning))
        {
            _logger.LogWarning("{Warning}", cudaWarning);
        }

        return new WorkerEngineOptions
        {
            MaxNumSeqs = Math.Max(1, s.MaxNumSeqs),
            MaxChunkSize = Math.Max(1, s.MaxChunkSize),
            MaxBatchedTokens = plan.MaxBatchedTokens,
            CudaVisibleDevices = cuda,
            ParallelismMode = plan.AppliedMode,
            GpuMemoryUtilization = s.GpuMemoryUtilization,
            GpuSplitGb = s.GpuSplitGb,
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

                if (_engine is ExLlamaV3WorkerEngine worker && !worker.IsWorkerAlive)
                {
                    if (!IsLoading && !worker.IsLoaded)
                    {
                        continue;
                    }

                    throw new InvalidOperationException(
                        IsLoading
                            ? "Python worker process died while loading the model."
                            : "Python worker process died while model was loaded.");
                }

                if (IsLoading || _engine is null || !_engine.IsLoaded)
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
            _lastLoadError =
                $"Engine restart limit ({MaxRestarts}) exceeded. Load the model again from Models.";
            _logger.LogCritical("Engine restart limit ({Max}) exceeded; manual intervention required", MaxRestarts);
            try
            {
                await UnloadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // best effort clear
            }

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

    private void OnWorkerLoadProgress(string phase, int pct) => SetLoadProgress(phase, pct);

    private void SetLoadProgress(string phase, int pct)
    {
        _loadPhase = phase;
        _loadProgressPct = Math.Clamp(pct, 0, 100);
        _loadStartedUtc ??= DateTime.UtcNow;
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
