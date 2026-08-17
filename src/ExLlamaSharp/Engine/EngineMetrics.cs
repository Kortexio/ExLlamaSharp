namespace ExLlamaSharp.Engine;

/// <summary>
/// Snapshot of scheduler / PageTable metrics from the inference engine.
/// </summary>
public sealed class EngineMetrics
{
    public long TotalPromptTokens { get; init; }
    public long TotalGeneratedTokens { get; init; }
    public long NumJobsWaiting { get; init; }
    public long NumJobsRunning { get; init; }
    public long NumJobsSwapped { get; init; }
    public long NumJobsFinished { get; init; }
    public long NumPagesUsed { get; init; }
    public long NumPagesFree { get; init; }
    public double TokensPerSecond { get; init; }
    public double LastStepMs { get; init; }
    public long StepCount { get; init; }
    public bool IsMock { get; init; }
}
