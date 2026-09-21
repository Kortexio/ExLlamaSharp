using ExLlamaSharp.Engine;

namespace ExLlamaSharp.Server.Services;

public sealed partial class EngineHostService
{
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
}