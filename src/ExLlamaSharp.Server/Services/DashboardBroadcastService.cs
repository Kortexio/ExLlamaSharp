using ExLlamaSharp.Server.Hubs;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ExLlamaSharp.Server.Services;

/// <summary>Pushes throttled metrics and job progress to <see cref="DashboardHub"/>.</summary>
public sealed class DashboardBroadcastService : BackgroundService
{
    private readonly IHubContext<DashboardHub> _hub;
    private readonly EngineHostService _engine;
    private readonly ModelJobsService _jobs;
    private readonly MetricsHistoryService _history;
    private readonly ILogger<DashboardBroadcastService> _logger;

    public DashboardBroadcastService(
        IHubContext<DashboardHub> hub,
        EngineHostService engine,
        ModelJobsService jobs,
        MetricsHistoryService history,
        ILogger<DashboardBroadcastService> logger)
    {
        _hub = hub;
        _engine = engine;
        _jobs = jobs;
        _history = history;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                double tps = 0;
                long waiting = 0;
                long running = 0;
                if (_engine.IsLoaded)
                {
                    try
                    {
                        var m = _engine.Engine.GetMetrics();
                        tps = m.TokensPerSecond;
                        waiting = m.NumJobsWaiting;
                        running = m.NumJobsRunning;
                    }
                    catch
                    {
                        // engine may be mid-reload
                    }
                }

                _history.RecordTps(tps);

                var jobList = await _jobs.ListAsync(stoppingToken).ConfigureAwait(false);
                var active = jobList
                    .Where(j => j.Status is "pending" or "running")
                    .Select(j => new
                    {
                        job_id = j.JobId,
                        type = j.Type,
                        status = j.Status,
                        progress_pct = j.ProgressPct,
                    })
                    .Take(20)
                    .ToList();

                await _hub.Clients.Group("default").SendAsync(
                    DashboardHub.MetricsMethod,
                    new
                    {
                        utc = DateTime.UtcNow,
                        tokens_per_second = tps,
                        jobs_waiting = waiting,
                        jobs_running = running,
                        model_loaded = _engine.IsLoaded,
                        active_jobs = active,
                        tps_series = _history.TpsSeries,
                        latency_p50_ms = _history.LatencyP50Ms,
                        latency_p95_ms = _history.LatencyP95Ms,
                        latency_series = _history.LatencySeries,
                    },
                    stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogDebug(ex, "Dashboard broadcast tick failed");
            }

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(1.5), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }
    }
}
