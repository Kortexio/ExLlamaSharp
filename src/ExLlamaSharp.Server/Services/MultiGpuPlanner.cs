using System.Globalization;
using ExLlamaSharp.Server.Data.Entities;

namespace ExLlamaSharp.Server.Services;

/// <summary>
/// Validates CUDA device lists and parallelism for the EXL3 worker (tensor / layer autosplit).
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
            "model" or "mp" => throw new InvalidOperationException(
                "ParallelismMode 'model' is not supported. Use 'tensor' or 'pipeline' with two or more GPUs."),
            _ => throw new ArgumentException($"Unknown parallelism mode: {parallelismMode}", nameof(parallelismMode)),
        };
    }

    public IReadOnlyList<int> ParseDeviceIds(string? cudaVisibleDevices)
    {
        if (string.IsNullOrWhiteSpace(cudaVisibleDevices))
        {
            return [];
        }

        var ids = new List<int>();
        foreach (var part in cudaVisibleDevices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id < 0)
            {
                throw new ArgumentException($"Invalid device id '{part}' in CudaVisibleDevices.", nameof(cudaVisibleDevices));
            }

            ids.Add(id);
        }

        return ids;
    }

    public IReadOnlyList<double>? ParseGpuSplitGb(string? gpuSplitGb, int deviceCount)
    {
        if (string.IsNullOrWhiteSpace(gpuSplitGb))
        {
            return null;
        }

        var values = new List<double>();
        foreach (var part in gpuSplitGb.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!double.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var gb) || gb < 0)
            {
                throw new ArgumentException($"Invalid GpuSplitGb entry '{part}'.", nameof(gpuSplitGb));
            }

            values.Add(gb);
        }

        if (deviceCount > 0 && values.Count != deviceCount)
        {
            throw new InvalidOperationException(
                $"GpuSplitGb has {values.Count} value(s) but CudaVisibleDevices lists {deviceCount} GPU(s).");
        }

        return values;
    }

    public MultiGpuPlan BuildPlan(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        if (settings.GpuMemoryUtilization is <= 0 or > 1)
        {
            throw new InvalidOperationException("GpuMemoryUtilization must be in (0, 1].");
        }

        var devices = ParseDeviceIds(settings.CudaVisibleDevices);
        var kind = ParseMode(settings.ParallelismMode);
        var split = ParseGpuSplitGb(settings.GpuSplitGb, devices.Count);

        if (kind != ParallelismKind.None && devices.Count < 2)
        {
            throw new InvalidOperationException(
                $"Parallelism mode '{settings.ParallelismMode}' requires at least 2 devices in CudaVisibleDevices (e.g. 0,1).");
        }

        return new MultiGpuPlan
        {
            Kind = kind,
            DeviceIds = devices.Count > 0 ? devices : [0],
            UsePerDeviceGb = split,
            GpuMemoryUtilization = settings.GpuMemoryUtilization,
            GpuSplitGb = string.IsNullOrWhiteSpace(settings.GpuSplitGb) ? null : settings.GpuSplitGb.Trim(),
            MaxNumSeqs = settings.MaxNumSeqs,
            MaxBatchedTokens = settings.MaxBatchedTokens,
            MaxChunkSize = settings.MaxChunkSize,
        };
    }

    public MultiGpuPlan BuildPlan(string? cudaVisibleDevices, string? parallelismMode, double gpuMemoryUtilization = 0.90, string? gpuSplitGb = null)
    {
        return BuildPlan(new AppSettings
        {
            CudaVisibleDevices = cudaVisibleDevices ?? "0",
            ParallelismMode = parallelismMode ?? "none",
            GpuMemoryUtilization = gpuMemoryUtilization,
            GpuSplitGb = gpuSplitGb,
        });
    }
}

public enum ParallelismKind
{
    None = 0,
    Tensor = 1,
    Pipeline = 2,
}

public sealed class MultiGpuPlan
{
    public ParallelismKind Kind { get; init; }
    public IReadOnlyList<int> DeviceIds { get; init; } = [0];
    public IReadOnlyList<double>? UsePerDeviceGb { get; init; }
    public string? GpuSplitGb { get; init; }
    public double GpuMemoryUtilization { get; init; } = 0.90;
    public int MaxNumSeqs { get; init; } = 256;
    public int MaxBatchedTokens { get; init; } = 8192;
    public int MaxChunkSize { get; init; } = 2048;

    public int NumDevices => DeviceIds.Count;
    public bool IsMultiGpu => Kind != ParallelismKind.None && NumDevices > 1;
}
