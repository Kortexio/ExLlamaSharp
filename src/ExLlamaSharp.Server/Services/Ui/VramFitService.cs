using System.Globalization;

namespace ExLlamaSharp.Server.Services.Ui;

public enum VramFitKind
{
    Pending,
    UnknownGpu,
    UnknownSize,
    Fits,
    Tight,
    TooLarge,
}

public sealed record VramFitResult(
    VramFitKind Kind,
    string Label,
    string Detail,
    string BadgeClass);

/// <summary>
/// Estimates whether an EXL3 model's weights can load on this GPU's total VRAM.
/// Does not rank or pick models — it only labels Fits / Tight / Too large.
/// </summary>
public sealed class VramFitService
{
    public const double DefaultGpuUtilization = 0.90;
    public const double RuntimeOverheadGb = 1.25;
    public const double KvFractionOfWeights = 0.12;
    public const double KvMinGb = 0.4;
    public const double KvMaxGb = 3.0;
    public const double FitsUsableFraction = 0.85;

    public VramFitResult Evaluate(long? weightBytes, GpuSnapshot? gpu, double gpuUtilization = DefaultGpuUtilization)
    {
        if (weightBytes is null)
        {
            return new VramFitResult(
                VramFitKind.Pending,
                "…",
                "Measuring model size…",
                "badge-muted");
        }

        if (weightBytes <= 0)
        {
            return UnknownSize();
        }

        var weightGb = weightBytes.Value / (1024d * 1024d * 1024d);
        return EvaluateGb(weightGb, gpu, gpuUtilization);
    }

    public VramFitResult EvaluateGb(double weightGb, GpuSnapshot? gpu, double gpuUtilization = DefaultGpuUtilization)
    {
        if (!TryUsableGb(gpu, gpuUtilization, out var totalGb, out var usableGb))
        {
            return UnknownGpu();
        }

        if (weightGb <= 0 || double.IsNaN(weightGb) || double.IsInfinity(weightGb))
        {
            return UnknownSize();
        }

        var kvGb = Math.Clamp(weightGb * KvFractionOfWeights, KvMinGb, KvMaxGb);
        var requiredGb = weightGb + RuntimeOverheadGb + kvGb;
        var kind = requiredGb <= usableGb * FitsUsableFraction
            ? VramFitKind.Fits
            : requiredGb <= usableGb
                ? VramFitKind.Tight
                : VramFitKind.TooLarge;

        var (label, badge) = kind switch
        {
            VramFitKind.Fits => ("Fits", "badge-ok"),
            VramFitKind.Tight => ("Tight", "badge-warn"),
            _ => ("Too large", "badge-err"),
        };

        var detail = kind == VramFitKind.TooLarge
            ? $"~{Gb(requiredGb)} GB estimated to load; this GPU has {Gb(usableGb)} GB usable ({Gb(totalGb)} GB × {gpuUtilization:P0}). Estimate only — not a guarantee."
            : $"~{Gb(requiredGb)} GB estimated of {Gb(usableGb)} GB usable ({Gb(totalGb)} GB × {gpuUtilization:P0}). Estimate only — not a guarantee.";

        return new VramFitResult(kind, label, detail, badge);
    }

    public static string FormatGpuCaption(GpuSnapshot? gpu, double gpuUtilization = DefaultGpuUtilization)
    {
        if (gpu is null || gpu.IsMock || gpu.MemoryTotalMb < 256)
        {
            return "GPU VRAM could not be read (nvidia-smi unavailable). Fit stays Unknown until a real GPU is detected. Models are not filtered or auto-selected.";
        }

        var totalGb = gpu.MemoryTotalMb / 1024d;
        var util = ClampUtilization(gpuUtilization);
        var usableGb = totalGb * util;
        return $"This GPU: {gpu.Name} · {Gb(totalGb)} GB total ({Gb(usableGb)} GB usable at {util:P0}). Fit is an estimate from weights + runtime + KV cache against total VRAM — it does not pick a model for you.";
    }

    private static bool TryUsableGb(GpuSnapshot? gpu, double gpuUtilization, out double totalGb, out double usableGb)
    {
        totalGb = 0;
        usableGb = 0;
        if (gpu is null || gpu.IsMock || gpu.MemoryTotalMb < 256)
        {
            return false;
        }

        totalGb = gpu.MemoryTotalMb / 1024d;
        usableGb = totalGb * ClampUtilization(gpuUtilization);
        return usableGb > 0;
    }

    private static double ClampUtilization(double gpuUtilization)
    {
        if (double.IsNaN(gpuUtilization) || double.IsInfinity(gpuUtilization) || gpuUtilization <= 0)
        {
            return DefaultGpuUtilization;
        }

        return Math.Clamp(gpuUtilization, 0.50, 1.0);
    }

    private static VramFitResult UnknownGpu() => new(
        VramFitKind.UnknownGpu,
        "Unknown",
        "Could not read this GPU's VRAM (nvidia-smi missing or mock).",
        "badge-muted");

    private static VramFitResult UnknownSize() => new(
        VramFitKind.UnknownSize,
        "Unknown",
        "Model size is not available, so VRAM fit cannot be estimated.",
        "badge-muted");

    private static string Gb(double value) =>
        value.ToString("0.0", CultureInfo.InvariantCulture);
}
