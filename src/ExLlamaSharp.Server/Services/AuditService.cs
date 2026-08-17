using System.Threading.Channels;
using ExLlamaSharp.Server.Data;
using ExLlamaSharp.Server.Data.Entities;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace ExLlamaSharp.Server.Services;

public sealed class AuditService : BackgroundService
{
    private readonly Channel<AuditLog> _channel = Channel.CreateUnbounded<AuditLog>(
        new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });

    private readonly IServiceScopeFactory _scopeFactory;
    private readonly ILogger<AuditService> _logger;

    public AuditService(IServiceScopeFactory scopeFactory, ILogger<AuditService> logger)
    {
        _scopeFactory = scopeFactory;
        _logger = logger;
    }

    public void Enqueue(AuditLog entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        if (!_channel.Writer.TryWrite(entry))
        {
            _logger.LogWarning("Failed to enqueue audit entry for {Endpoint}", entry.Endpoint);
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var batch = new List<AuditLog>(128);
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));

        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken).ConfigureAwait(false))
            {
                while (_channel.Reader.TryRead(out var entry))
                {
                    batch.Add(entry);
                }

                if (batch.Count == 0)
                {
                    continue;
                }

                try
                {
                    await using var scope = _scopeFactory.CreateAsyncScope();
                    var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
                    db.AuditLogs.AddRange(batch);
                    await db.SaveChangesAsync(stoppingToken).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is not OperationCanceledException)
                {
                    _logger.LogError(ex, "Failed to flush {Count} audit log entries", batch.Count);
                }
                finally
                {
                    batch.Clear();
                }
            }
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            // drain remaining on shutdown
        }

        await FlushRemainingAsync().ConfigureAwait(false);
    }

    private async Task FlushRemainingAsync()
    {
        var remaining = new List<AuditLog>();
        while (_channel.Reader.TryRead(out var entry))
        {
            remaining.Add(entry);
        }

        if (remaining.Count == 0)
        {
            return;
        }

        try
        {
            await using var scope = _scopeFactory.CreateAsyncScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AuditLogs.AddRange(remaining);
            await db.SaveChangesAsync().ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to flush remaining audit entries on shutdown");
        }
    }
}
