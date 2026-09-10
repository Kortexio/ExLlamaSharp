namespace ExLlamaSharp.Engine;

/// <summary>
/// Scheduler / cache knobs forwarded to the ExLlamaV3 Python worker at load time.
/// </summary>
public sealed class WorkerEngineOptions
{
    /// <summary>Max sequences that may share a forward pass (Generator.max_batch_size).</summary>
    public int MaxNumSeqs { get; init; } = 256;

    /// <summary>Prefill chunk size (Generator.max_chunk_size).</summary>
    public int MaxChunkSize { get; init; } = 2048;

    /// <summary>
    /// Total KV cache tokens shared by all live sequences (Cache.max_num_tokens).
    /// Not per-sequence: 8 users at 4k context need ~32k.
    /// </summary>
    public int MaxBatchedTokens { get; init; } = 8192;

    /// <summary>Override path to worker.py (tests / fake worker).</summary>
    public string? WorkerScript { get; init; }

    /// <summary>Override Python executable.</summary>
    public string? PythonPath { get; init; }

    /// <summary>Comma-separated CUDA device ids (PCI / nvidia-smi). Remapped strongest-first at spawn.</summary>
    public string? CudaVisibleDevices { get; init; }

    /// <summary>none | tensor | pipeline</summary>
    public string ParallelismMode { get; init; } = "none";

    /// <summary>Fraction of each GPU's VRAM for auto split (0, 1].</summary>
    public double GpuMemoryUtilization { get; init; } = 0.90;

    /// <summary>Optional GB per remapped visible GPU (CSV). Empty = auto.</summary>
    public string? GpuSplitGb { get; init; }

    public bool SpeculativeEnabled { get; init; }
    public string? DraftModelPath { get; init; }
    public int DraftK { get; init; } = 5;
}
