using System.Collections.Concurrent;
using ExLlamaSharp.Server.Data;
using ExLlamaSharp.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace ExLlamaSharp.Server.Services;

/// <summary>
/// Pull/quantize/import job queue with real progress where possible.
/// </summary>
public sealed class ModelJobsService
{
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<ModelJobsService> _logger;
    private readonly PythonModelTools _python;
    private readonly SettingsService _settings;
    private readonly IConfiguration _config;
    private readonly HuggingFaceCatalogService _hf;
    private readonly ConcurrentDictionary<Guid, CancellationTokenSource> _running = new();
    private readonly ConcurrentDictionary<Guid, PullJobMeta> _meta = new();

    public ModelJobsService(
        IServiceScopeFactory scopeFactory,
        ILogger<ModelJobsService> logger,
        PythonModelTools python,
        SettingsService settings,
        IConfiguration config,
        HuggingFaceCatalogService hf)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
        _python = python;
        _settings = settings;
        _config = config;
        _hf = hf;
    }

    public async Task<ModelJob> EnqueuePullAsync(string repoId, string? branch = null, bool quantize = false, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        return await CreateAndRunAsync("pull", null, async (job, ct) =>
        {
            var settings = await _settings.GetAsync(ct).ConfigureAwait(false);
            var root = string.IsNullOrWhiteSpace(settings.ModelsPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ExLlamaSharp", "models")
                : settings.ModelsPath;
            Directory.CreateDirectory(root);

            var folder = repoId.Replace('\\', '/').Split('/', StringSplitOptions.RemoveEmptyEntries).Last();
            var dest = Path.Combine(root, folder);
            Directory.CreateDirectory(dest);

            var revision = await _hf.ResolveRevisionAsync(repoId, branch, ct).ConfigureAwait(false);
            var info = await _hf.GetRevisionInfoAsync(repoId, revision, ct).ConfigureAwait(false);
            var meta = _meta.AddOrUpdate(
                job.JobId,
                _ => new PullJobMeta
                {
                    RepoId = repoId,
                    Revision = revision,
                    ParameterLabel = info.ParameterLabel,
                    BytesTotal = info.BytesTotal,
                },
                (_, existing) =>
                {
                    existing.RepoId = repoId;
                    existing.Revision = revision;
                    existing.ParameterLabel = info.ParameterLabel;
                    existing.BytesTotal = info.BytesTotal;
                    return existing;
                });
            await UpdateStatusAsync(job.JobId, "running", 5, null, FormatPullDetail(meta)).ConfigureAwait(false);
            var psi = _python.CreatePullStartInfo(repoId, dest, revision);
            var token = HuggingFaceCatalogService.ResolveToken(_config);
            if (!string.IsNullOrWhiteSpace(token))
            {
                psi.Environment["HF_TOKEN"] = token;
                psi.Environment["HUGGING_FACE_HUB_TOKEN"] = token;
            }

            _logger.LogInformation("Pulling {Repo}@{Rev} -> {Dest}", repoId, revision, dest);
            using var pulseCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var pulse = PulsePullProgressAsync(job.JobId, dest, pulseCts.Token);
            int code;
            string stdout;
            string stderr;
            try
            {
                (code, stdout, stderr) = await _python.RunAsync(psi, ct).ConfigureAwait(false);
            }
            finally
            {
                await pulseCts.CancelAsync().ConfigureAwait(false);
                try { await pulse.ConfigureAwait(false); } catch (OperationCanceledException) { }
            }
            if (code != 0)
            {
                var err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidOperationException(err.Length > 800 ? err[^800..] : err);
            }

            var hasWeights = File.Exists(Path.Combine(dest, "config.json"))
                && Directory.EnumerateFiles(dest, "*.safetensors", SearchOption.TopDirectoryOnly).Any();
            if (!hasWeights)
            {
                throw new InvalidOperationException(
                    $"Download of {repoId}@{revision} finished but no EXL3 weights were found (config.json / *.safetensors). " +
                    "This repo likely stores quants on branches like 4.00bpw.");
            }

            await using var scope = _scopeFactory.CreateAsyncScope();
            var inventory = scope.ServiceProvider.GetRequiredService<ModelInventoryService>();
            await inventory.EnsureRecordAsync(dest, folder, ct).ConfigureAwait(false);
            _logger.LogInformation("Pull completed for {Repo}: {Out}", repoId, stdout.Trim());
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ModelJob> EnqueueQuantizeAsync(Guid modelId, double bits = 4.0, CancellationToken cancellationToken = default)
    {
        return await CreateAndRunAsync("quantize", modelId, async (job, ct) =>
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var record = await db.Models.AsNoTracking()
                .FirstOrDefaultAsync(m => m.Id == modelId, ct)
                .ConfigureAwait(false)
                ?? throw new InvalidOperationException($"Model {modelId} not found.");

            if (!Directory.Exists(record.Path))
            {
                throw new InvalidOperationException($"Model path missing: {record.Path}");
            }

            var settings = await _settings.GetAsync(ct).ConfigureAwait(false);
            var root = string.IsNullOrWhiteSpace(settings.ModelsPath)
                ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "ExLlamaSharp", "models")
                : settings.ModelsPath;
            Directory.CreateDirectory(root);

            var outName = $"{Path.GetFileName(record.Path.TrimEnd(Path.DirectorySeparatorChar))}-exl3-{bits.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}bpw";
            var outputPath = Path.Combine(root, outName);
            var workPath = Path.Combine(root, ".quantize-work", job.JobId.ToString("N"));
            Directory.CreateDirectory(workPath);

            await UpdateStatusAsync(job.JobId, "running", 10, null, $"Quantizing → {outName}").ConfigureAwait(false);
            var psi = _python.CreateConvertStartInfo(record.Path, outputPath, workPath, bits);
            var (code, stdout, stderr) = await _python.RunAsync(psi, ct).ConfigureAwait(false);
            if (code != 0)
            {
                var err = string.IsNullOrWhiteSpace(stderr) ? stdout : stderr;
                throw new InvalidOperationException(err.Length > 800 ? err[^800..] : err);
            }

            await UpdateStatusAsync(job.JobId, "running", 90, null, "Registering quantized model").ConfigureAwait(false);
            var inventory = scope.ServiceProvider.GetRequiredService<ModelInventoryService>();
            await inventory.EnsureRecordAsync(outputPath, outName, ct).ConfigureAwait(false);
            _logger.LogInformation("Quantize completed for {ModelId} → {Out}", modelId, outputPath);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ModelJob> EnqueueImportAsync(string sourcePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourcePath);
        return await CreateAndRunAsync("import", null, async (job, ct) =>
        {
            var path = Path.GetFullPath(sourcePath.Trim());
            await UpdateStatusAsync(job.JobId, "running", 20, null, "Validating folder").ConfigureAwait(false);
            if (!Directory.Exists(path))
            {
                throw new InvalidOperationException($"Source path not found: {path}");
            }

            if (!ExLlamaSharp.Engine.ExLlamaV3WorkerEngine.LooksLikeExl3Directory(path)
                && !File.Exists(Path.Combine(path, "config.json")))
            {
                throw new InvalidOperationException(
                    "Folder does not look like an EXL3/HF model (need config.json and weights).");
            }

            await UpdateStatusAsync(job.JobId, "running", 60, null, "Registering model").ConfigureAwait(false);
            await using var scope = _scopeFactory.CreateAsyncScope();
            var inventory = scope.ServiceProvider.GetRequiredService<ModelInventoryService>();
            var alias = Path.GetFileName(path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            await inventory.EnsureRecordAsync(path, alias, ct).ConfigureAwait(false);
            await UpdateStatusAsync(job.JobId, "running", 95, null, path).ConfigureAwait(false);
            _logger.LogInformation("Import completed for {Path}", path);
        }, cancellationToken).ConfigureAwait(false);
    }

    public async Task<ModelJob?> GetAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ModelJobs.AsNoTracking().FirstOrDefaultAsync(j => j.JobId == jobId, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ModelJob>> ListAsync(CancellationToken cancellationToken = default)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        return await db.ModelJobs.AsNoTracking()
            .OrderByDescending(j => j.CreatedAt)
            .Take(100)
            .ToListAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public bool TryGetPullMeta(Guid jobId, out PullJobMeta? meta) => _meta.TryGetValue(jobId, out meta);

    public sealed class PullJobMeta
    {
        public string? RepoId { get; set; }
        public string? Revision { get; set; }
        public string? ParameterLabel { get; set; }
        public long BytesTotal { get; set; }
        public long BytesDownloaded { get; set; }
    }

    public async Task<bool> CancelAsync(Guid jobId, CancellationToken cancellationToken = default)
    {
        if (_running.TryGetValue(jobId, out var cts))
        {
            await cts.CancelAsync().ConfigureAwait(false);
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.ModelJobs.FirstOrDefaultAsync(j => j.JobId == jobId, cancellationToken).ConfigureAwait(false);
        if (job is null)
        {
            return false;
        }

        if (job.Status is "completed" or "failed" or "cancelled")
        {
            return false;
        }

        job.Status = "cancelled";
        job.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        return true;
    }

    private async Task<ModelJob> CreateAndRunAsync(
        string type,
        Guid? modelId,
        Func<ModelJob, CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        var job = new ModelJob
        {
            JobId = Guid.NewGuid(),
            Type = type,
            Status = "pending",
            ProgressPct = 0,
            ModelId = modelId,
            CreatedAt = DateTime.UtcNow,
            UpdatedAt = DateTime.UtcNow,
        };

        await using (var scope = _scopeFactory.CreateAsyncScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.ModelJobs.Add(job);
            await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        }

        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _running[job.JobId] = cts;

        _ = Task.Run(async () =>
        {
            try
            {
                await UpdateStatusAsync(job.JobId, "running", 0, null, null).ConfigureAwait(false);
                await work(job, cts.Token).ConfigureAwait(false);
                if (!cts.IsCancellationRequested)
                {
                    _meta.TryGetValue(job.JobId, out var done);
                    if (done is not null && done.BytesTotal > 0)
                    {
                        done.BytesDownloaded = done.BytesTotal;
                    }

                    await UpdateStatusAsync(job.JobId, "completed", 100, 0, done is null ? null : FormatPullDetail(done, done: true)).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException)
            {
                await UpdateStatusAsync(job.JobId, "cancelled", null, null, null).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Model job {JobId} failed", job.JobId);
                await UpdateStatusAsync(job.JobId, "failed", null, null, ex.Message).ConfigureAwait(false);
            }
            finally
            {
                _running.TryRemove(job.JobId, out _);
                cts.Dispose();
            }
        }, CancellationToken.None);

        return job;
    }

    private async Task PulsePullProgressAsync(Guid jobId, string dest, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromSeconds(2), cancellationToken).ConfigureAwait(false);
            var downloaded = MeasureDownloadedBytes(dest);
            _meta.AddOrUpdate(
                jobId,
                _ => new PullJobMeta { BytesDownloaded = downloaded },
                (_, existing) =>
                {
                    existing.BytesDownloaded = downloaded;
                    return existing;
                });
            _meta.TryGetValue(jobId, out var meta);
            var total = meta?.BytesTotal ?? 0;
            var pct = total > 0
                ? Math.Clamp(5 + 90.0 * downloaded / total, 5, 99)
                : Math.Min(92, 8 + downloaded / (1024d * 1024d * 50));
            await UpdateStatusAsync(jobId, "running", pct, null, FormatPullDetail(meta)).ConfigureAwait(false);
        }
    }

    private static string FormatPullDetail(PullJobMeta? meta, bool done = false)
    {
        if (meta is null)
        {
            return "Downloading…";
        }

        var name = string.IsNullOrWhiteSpace(meta.Revision)
            ? meta.RepoId
            : $"{meta.RepoId}@{meta.Revision}";
        var size = meta.BytesTotal > 0
            ? done
                ? HuggingFaceCatalogService.FormatBytes(meta.BytesTotal)
                : $"{HuggingFaceCatalogService.FormatBytes(meta.BytesDownloaded)} / {HuggingFaceCatalogService.FormatBytes(meta.BytesTotal)}"
            : HuggingFaceCatalogService.FormatBytes(meta.BytesDownloaded);
        var parms = string.IsNullOrWhiteSpace(meta.ParameterLabel) ? null : meta.ParameterLabel;
        return string.IsNullOrWhiteSpace(parms)
            ? $"{name} · {size}"
            : $"{name} · {parms} · {size}";
    }

    private static long MeasureDownloadedBytes(string dest)
    {
        if (!Directory.Exists(dest))
        {
            return 0;
        }

        long bytes = 0;
        foreach (var file in Directory.EnumerateFiles(dest, "*", SearchOption.AllDirectories))
        {
            var inCache = file.Contains($"{Path.DirectorySeparatorChar}.cache{Path.DirectorySeparatorChar}", StringComparison.OrdinalIgnoreCase);
            if (inCache && !file.EndsWith(".incomplete", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            try
            {
                bytes += new FileInfo(file).Length;
            }
            catch
            {
                // skip locked files
            }
        }

        return bytes;
    }

    private async Task SimulateProgressAsync(Guid jobId, CancellationToken cancellationToken)
    {
        for (var pct = 10; pct <= 90; pct += 10)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var eta = (100 - pct) / 10 * 2;
            await UpdateStatusAsync(jobId, "running", pct, eta, null).ConfigureAwait(false);
            await Task.Delay(TimeSpan.FromMilliseconds(200), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UpdateStatusAsync(Guid jobId, string status, double? progress, int? eta, string? error)
    {
        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var job = await db.ModelJobs.FirstOrDefaultAsync(j => j.JobId == jobId).ConfigureAwait(false);
        if (job is null)
        {
            return;
        }

        job.Status = status;
        if (progress is not null)
        {
            job.ProgressPct = progress.Value;
        }

        job.EtaSeconds = eta;
        if (error is not null)
        {
            job.Error = error;
        }
        else if (status is "completed")
        {
            job.Error = null;
        }

        job.UpdatedAt = DateTime.UtcNow;
        await db.SaveChangesAsync().ConfigureAwait(false);
    }
}
