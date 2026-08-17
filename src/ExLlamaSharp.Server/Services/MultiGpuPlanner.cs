using ExLlamaSharp.Server.Data.Entities;

namespace ExLlamaSharp.Server.Services;

/// <summary>
/// Helpers for tensor / pipeline / model parallelism configuration (TP / PP / MP).
/// Stub: validates and maps settings; native NCCL wiring happens in exllamasharp.dll.
/// </summary>
public sealed class MultiGpuPlanner
{
    public ParallelismKind ParseMode(string? parallelismMode)
    {
        if (string.IsNullOrWhiteSpace(parallelismMode))
        {
            return ParallelismKind.None;
        }

        return parallelismMode.Trim().ToLowerInvariant() switch
        {
            "none" or "single" => ParallelismKind.None,
            "tensor" or "tp" => ParallelismKind.Tensor,
            "pipeline" or "pipe" or "pp" => ParallelismKind.Pipeline,
            "model" or "mp" => ParallelismKind.Model,
            _ => throw new ArgumentException($"Unknown parallelism mode: {parallelismMode}", nameof(parallelismMode)),
        };
    }

    public IReadOnlyList<int> ParseDeviceIds(string? cudaVisibleDevices)
    {
        if (string.IsNullOrWhiteSpace(cudaVisibleDevices))
        {
            return [0];
        }

        var ids = new List<int>();
        foreach (var part in cudaVisibleDevices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, out var id) || id < 0)
            {
                throw new ArgumentException($"Invalid device id '{part}' in CudaVisibleDevices.", nameof(cudaVisibleDevices));
            }

            ids.Add(id);
        }

        return ids.Count > 0 ? ids : [0];
    }

    public MultiGpuPlan BuildPlan(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var devices = ParseDeviceIds(settings.CudaVisibleDevices);
        var kind = ParseMode(settings.ParallelismMode);

        if (kind != ParallelismKind.None && devices.Count < 2)
        {
            throw new InvalidOperationException(
                $"Parallelism mode '{settings.ParallelismMode}' requires at least 2 devices in CudaVisibleDevices.");
        }

        return new MultiGpuPlan
        {
            Kind = kind,
            DeviceIds = devices,
            GpuMemoryUtilization = settings.GpuMemoryUtilization,
            MaxNumSeqs = settings.MaxNumSeqs,
            MaxBatchedTokens = settings.MaxBatchedTokens,
            MaxChunkSize = settings.MaxChunkSize,
        };
    }

    public MultiGpuPlan BuildPlan(string? cudaVisibleDevices, string? parallelismMode, double gpuMemoryUtilization = 0.90)
    {
        return BuildPlan(new AppSettings
        {
            CudaVisibleDevices = cudaVisibleDevices ?? "0",
            ParallelismMode = parallelismMode ?? "none",
            GpuMemoryUtilization = gpuMemoryUtilization,
        });
    }

    /// <summary>
    /// Maps to native <c>ExlParallelism</c> int values (None=0, Tensor=1, Pipe=2, Model=3).
    /// </summary>
    public int ToNativeInt(ParallelismKind kind) => (int)kind;
}

public enum ParallelismKind
{
    None = 0,
    Tensor = 1,
    Pipeline = 2,
    Model = 3,
}

public sealed class MultiGpuPlan
{
    public ParallelismKind Kind { get; init; }
    public IReadOnlyList<int> DeviceIds { get; init; } = [0];
    public double GpuMemoryUtilization { get; init; } = 0.90;
    public int MaxNumSeqs { get; init; } = 256;
    public int MaxBatchedTokens { get; init; } = 8192;
    public int MaxChunkSize { get; init; } = 2048;

    public int NumDevices => DeviceIds.Count;
    public bool IsMultiGpu => Kind != ParallelismKind.None && NumDevices > 1;
}
