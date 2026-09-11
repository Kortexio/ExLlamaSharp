using System.Globalization;
using ExLlamaSharp.Server.Services;

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

public sealed record ModelLoadProfile(
    string Id,
    string Label,
    int MaxBatchedTokens,
    string ParallelismMode,
    string? GpuSplitGb,
    VramFitKind Kind,
    string BadgeClass,
    bool Available,
    string Summary);

public sealed record ModelLoadProfilesResult(
    IReadOnlyList<ModelLoadProfile> Profiles,
    int CustomMaxBatchedTokens,
    string CustomParallelismMode,
    string? CustomGpuSplitGb,
    string? RecommendedProfileId);

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
    public const double KvMaxGb = 16.0;
    public const double FitsUsableFraction = 0.85;
    public const int DefaultContextTokens = 4096;

    public VramFitResult Evaluate(long? weightBytes, GpuSnapshot? gpu, double gpuUtilization = DefaultGpuUtilization)
        => Evaluate(weightBytes, gpu is null ? null : new[] { gpu }, gpuUtilization);

    public VramFitResult Evaluate(
        long? weightBytes,
        IReadOnlyList<GpuSnapshot>? gpus,
        double gpuUtilization = DefaultGpuUtilization,
        string? gpuSplitGb = null,
        int maxBatchedTokens = 0)
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
        return EvaluateGb(weightGb, gpus, gpuUtilization, gpuSplitGb, maxBatchedTokens);
    }

    public VramFitResult EvaluateGb(double weightGb, GpuSnapshot? gpu, double gpuUtilization = DefaultGpuUtilization)
        => EvaluateGb(weightGb, gpu is null ? null : new[] { gpu }, gpuUtilization);

    public VramFitResult EvaluateGb(
        double weightGb,
        IReadOnlyList<GpuSnapshot>? gpus,
        double gpuUtilization = DefaultGpuUtilization,
        string? gpuSplitGb = null,
        int maxBatchedTokens = 0)
    {
        if (!TryUsableGb(gpus, gpuUtilization, gpuSplitGb, out var totalGb, out var usableGb))
        {
            return UnknownGpu();
        }

        if (weightGb <= 0 || double.IsNaN(weightGb) || double.IsInfinity(weightGb))
        {
            return UnknownSize();
        }

        var kvGb = EstimateKvGb(weightGb, maxBatchedTokens);
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

        var ctx = NormalizeContext(maxBatchedTokens);
        var multi = (gpus?.Count ?? 0) > 1;
        var where = multi ? "visible GPUs" : "this GPU";
        var ctxBit = ctx != DefaultContextTokens ? $" including {ctx} context tokens" : "";
        var detail = kind == VramFitKind.TooLarge
            ? $"~{Gb(requiredGb)} GB estimated to load{ctxBit}; {(multi ? "visible GPUs have" : "this GPU has")} {Gb(usableGb)} GB usable ({Gb(totalGb)} GB × {gpuUtilization:P0}). Estimate only — not a guarantee."
            : $"~{Gb(requiredGb)} GB estimated of {Gb(usableGb)} GB usable on {where}{ctxBit} ({Gb(totalGb)} GB × {gpuUtilization:P0}). Estimate only — not a guarantee.";

        return new VramFitResult(kind, label, detail, badge);
    }

    /// <summary>
    /// Hard refuse only when weights plus this context clearly cannot fit.
    /// Slightly over-estimate is allowed so a known-good 32B@2048 load on 12+8 GB is not blocked.
    /// </summary>
    public bool TryExplainLoadRefusal(
        double weightGb,
        IReadOnlyList<GpuSnapshot>? gpus,
        double gpuUtilization,
        string? gpuSplitGb,
        int maxBatchedTokens,
        out string error)
    {
        error = "";
        if (weightGb <= 0 || !TryUsableGb(gpus, gpuUtilization, gpuSplitGb, out _, out var usableGb))
        {
            return false;
        }

        var leftover = usableGb - weightGb;
        if (leftover < 0.15)
        {
            error =
                $"Model weights (~{Gb(weightGb)} GB) do not fit the visible GPU split ({Gb(usableGb)} GB usable). " +
                "Use more GPUs, a smaller quant, or raise GpuSplitGb.";
            return true;
        }

        var ctx = NormalizeContext(maxBatchedTokens);
        var kvGb = EstimateKvGb(weightGb, ctx);
        if (kvGb <= leftover + 0.5)
        {
            return false;
        }

        var suggested = SuggestMaxBatchedTokens(weightGb, leftover, ctx);
        error =
            $"Insufficient VRAM for ~{Gb(weightGb)} GB weights + {ctx} context tokens " +
            $"(KV ~{Gb(kvGb)} GB, {Gb(usableGb)} GB usable / {Gb(leftover)} GB left after weights). " +
            $"Lower Max batched tokens to {suggested} or less, or use pipeline with a smaller model. " +
            "On Windows use pipeline — tensor parallel times out.";
        return true;
    }

    public static int SuggestMaxBatchedTokens(double weightGb, double leftoverGb, int requested)
    {
        if (weightGb <= 0 || leftoverGb <= 0)
        {
            return 2048;
        }

        var raw = leftoverGb * DefaultContextTokens / (weightGb * KvFractionOfWeights);
        var aligned = Math.Max(256, ((int)Math.Floor(raw) / 256) * 256);
        var req = requested > 0 ? requested : DefaultContextTokens;
        return Math.Clamp(aligned, 256, Math.Max(256, req));
    }

    public const string ProfileConservative = "conservative";
    public const string ProfileNormal = "normal";
    public const string ProfileAggressive = "aggressive";
    public const string ProfileCustom = "custom";
    public const int ProfileContextCeiling = 32768;

    public static string NormalizeLoadProfile(string? profile)
    {
        if (string.IsNullOrWhiteSpace(profile))
        {
            return ProfileNormal;
        }

        return profile.Trim().ToLowerInvariant() switch
        {
            "conservative" or "conservadora" => ProfileConservative,
            "aggressive" or "agressiva" => ProfileAggressive,
            "custom" or "personalizado" => ProfileCustom,
            "normal" => ProfileNormal,
            _ => ProfileNormal,
        };
    }

    /// <summary>
    /// Builds conservative / normal / aggressive context presets for the current GPU split.
    /// </summary>
    public ModelLoadProfilesResult BuildLoadProfiles(
        double weightGb,
        IReadOnlyList<GpuSnapshot>? gpus,
        double gpuUtilization,
        string? gpuSplitGb,
        string? cudaVisibleDevices = null,
        int customMaxBatchedTokens = 0,
        string? customParallelismMode = null)
    {
        var util = ClampUtilization(gpuUtilization);
        var visible = GpuInfoService.FilterVisible(gpus ?? [], cudaVisibleDevices);
        var deviceCount = visible.Count > 0
            ? visible.Count
            : Math.Max(1, CountCudaDevices(cudaVisibleDevices));
        var parallelism = ResolveProfileParallelism(deviceCount);
        var effectiveSplit = string.IsNullOrWhiteSpace(gpuSplitGb)
            ? BuildAutoSplitGb(gpus, cudaVisibleDevices, util)
            : gpuSplitGb.Trim();

        var customTokens = MultiGpuPlanner.AlignCacheTokens(
            customMaxBatchedTokens > 0 ? customMaxBatchedTokens : DefaultContextTokens);
        var customMode = string.IsNullOrWhiteSpace(customParallelismMode)
            ? parallelism
            : (OperatingSystem.IsWindows()
               && deviceCount >= 2
               && customParallelismMode.Trim().Equals("tensor", StringComparison.OrdinalIgnoreCase)
                ? "pipeline"
                : customParallelismMode.Trim().ToLowerInvariant());

        if (weightGb <= 0 || !TryUsableGb(visible.Count > 0 ? visible : gpus, util, effectiveSplit, out _, out var usableGb))
        {
            var unavailable = new[]
            {
                UnavailableProfile(ProfileConservative, "Conservadora", parallelism, effectiveSplit),
                UnavailableProfile(ProfileNormal, "Normal", parallelism, effectiveSplit),
                UnavailableProfile(ProfileAggressive, "Agressiva", parallelism, effectiveSplit),
            };
            return new ModelLoadProfilesResult(unavailable, customTokens, customMode, effectiveSplit, null);
        }

        var leftover = usableGb - weightGb;
        if (leftover < 0.15)
        {
            var unavailable = new[]
            {
                UnavailableProfile(ProfileConservative, "Conservadora", parallelism, effectiveSplit),
                UnavailableProfile(ProfileNormal, "Normal", parallelism, effectiveSplit),
                UnavailableProfile(ProfileAggressive, "Agressiva", parallelism, effectiveSplit),
            };
            return new ModelLoadProfilesResult(unavailable, customTokens, customMode, effectiveSplit, null);
        }

        var maxTokens = FindMaxFittingTokens(weightGb, visible, util, effectiveSplit, leftover);
        if (maxTokens is null)
        {
            var unavailable = new[]
            {
                UnavailableProfile(ProfileConservative, "Conservadora", parallelism, effectiveSplit),
                UnavailableProfile(ProfileNormal, "Normal", parallelism, effectiveSplit),
                UnavailableProfile(ProfileAggressive, "Agressiva", parallelism, effectiveSplit),
            };
            return new ModelLoadProfilesResult(unavailable, customTokens, customMode, effectiveSplit, null);
        }

        var aggressiveTokens = maxTokens.Value;
        var conservativeTokens = ScaleProfileTokens(aggressiveTokens, 0.50, preferMin2048: true);
        var normalTokens = ScaleProfileTokens(aggressiveTokens, 0.75, preferMin2048: false);

        var profiles = new[]
        {
            BuildProfile(ProfileConservative, "Conservadora", weightGb, visible, util, effectiveSplit, parallelism, conservativeTokens),
            BuildProfile(ProfileNormal, "Normal", weightGb, visible, util, effectiveSplit, parallelism, normalTokens),
            BuildProfile(ProfileAggressive, "Agressiva", weightGb, visible, util, effectiveSplit, parallelism, aggressiveTokens),
        };

        var recommended = profiles.LastOrDefault(p => p.Available && p.Kind is VramFitKind.Fits or VramFitKind.Tight)?.Id
            ?? profiles.FirstOrDefault(p => p.Available)?.Id;

        return new ModelLoadProfilesResult(profiles, customTokens, customMode, effectiveSplit, recommended);
    }

    public ModelLoadProfile? GetProfile(ModelLoadProfilesResult result, string? profileId)
    {
        var id = NormalizeLoadProfile(profileId);
        if (id == ProfileCustom)
        {
            return null;
        }

        return result.Profiles.FirstOrDefault(p => p.Id == id)
            ?? result.Profiles.FirstOrDefault(p => p.Id == ProfileNormal);
    }

    private ModelLoadProfile BuildProfile(
        string id,
        string label,
        double weightGb,
        IReadOnlyList<GpuSnapshot>? gpus,
        double util,
        string? split,
        string parallelism,
        int tokens)
    {
        var fit = EvaluateGb(weightGb, gpus, util, split, tokens);
        var refused = TryExplainLoadRefusal(weightGb, gpus, util, split, tokens, out var refuseDetail);
        var available = !refused;
        var summary = available
            ? $"{tokens} tokens · {parallelism} · {fit.Label}"
            : (string.IsNullOrWhiteSpace(refuseDetail) ? fit.Detail : refuseDetail);
        return new ModelLoadProfile(
            id,
            label,
            tokens,
            parallelism,
            split,
            available ? fit.Kind : VramFitKind.TooLarge,
            available ? fit.BadgeClass : "badge-err",
            available,
            summary);
    }

    /// <summary>
    /// Largest aligned context that passes <see cref="TryExplainLoadRefusal"/> (same gate as EngineHost).
    /// </summary>
    private int? FindMaxFittingTokens(
        double weightGb,
        IReadOnlyList<GpuSnapshot>? gpus,
        double util,
        string? split,
        double leftoverGb)
    {
        var candidate = MultiGpuPlanner.AlignCacheTokens(
            SuggestMaxBatchedTokens(weightGb, leftoverGb, ProfileContextCeiling));
        while (candidate >= 256)
        {
            if (!TryExplainLoadRefusal(weightGb, gpus, util, split, candidate, out _))
            {
                return candidate;
            }

            var next = MultiGpuPlanner.AlignCacheTokens(candidate - 256);
            if (next >= candidate)
            {
                break;
            }

            candidate = next;
        }

        return null;
    }

    private static ModelLoadProfile UnavailableProfile(string id, string label, string parallelism, string? split) =>
        new(
            id,
            label,
            0,
            parallelism,
            split,
            VramFitKind.TooLarge,
            "badge-err",
            false,
            "Model weights do not fit the visible GPU split.");

    private static int ScaleProfileTokens(int maxTokens, double fraction, bool preferMin2048)
    {
        var raw = (int)Math.Floor(maxTokens * fraction);
        if (preferMin2048 && maxTokens >= 2048)
        {
            raw = Math.Max(2048, raw);
        }

        raw = Math.Clamp(raw, 256, maxTokens);
        return MultiGpuPlanner.AlignCacheTokens(raw);
    }

    private static string ResolveProfileParallelism(int deviceCount)
    {
        if (deviceCount < 2)
        {
            return "none";
        }

        // Windows TP times out; profiles always pick pipeline for multi-GPU.
        return "pipeline";
    }

    private static int CountCudaDevices(string? cudaVisibleDevices)
    {
        if (string.IsNullOrWhiteSpace(cudaVisibleDevices))
        {
            return 0;
        }

        return cudaVisibleDevices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Length;
    }

    /// <summary>Auto GpuSplitGb in CudaVisibleDevices order (PCI indices).</summary>
    public static string? BuildAutoSplitGb(
        IReadOnlyList<GpuSnapshot>? gpus,
        string? cudaVisibleDevices,
        double gpuUtilization = DefaultGpuUtilization)
    {
        if (gpus is null || gpus.Count == 0)
        {
            return null;
        }

        var util = ClampUtilization(gpuUtilization);
        var byIndex = gpus
            .Where(g => !g.IsMock && g.MemoryTotalMb >= 256)
            .GroupBy(g => g.Index)
            .ToDictionary(g => g.Key, g => g.First());

        IEnumerable<int> order;
        if (string.IsNullOrWhiteSpace(cudaVisibleDevices))
        {
            order = byIndex.Keys.OrderBy(i => i);
        }
        else
        {
            var ids = new List<int>();
            foreach (var part in cudaVisibleDevices.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id))
                {
                    ids.Add(id);
                }
            }

            order = ids;
        }

        var values = new List<string>();
        foreach (var id in order)
        {
            if (!byIndex.TryGetValue(id, out var gpu))
            {
                return null;
            }

            var gb = gpu.MemoryTotalMb / 1024d * util;
            values.Add(gb.ToString("0.###", CultureInfo.InvariantCulture));
        }

        return values.Count == 0 ? null : string.Join(",", values);
    }

    public static double EstimateKvGb(double weightGb, int maxBatchedTokens)
    {
        var scale = NormalizeContext(maxBatchedTokens) / (double)DefaultContextTokens;
        return Math.Clamp(weightGb * KvFractionOfWeights * scale, KvMinGb, KvMaxGb);
    }

    public static int NormalizeContext(int maxBatchedTokens)
        => maxBatchedTokens > 0 ? Math.Max(256, maxBatchedTokens) : DefaultContextTokens;

    public static string FormatGpuCaption(GpuSnapshot? gpu, double gpuUtilization = DefaultGpuUtilization)
        => FormatGpuCaption(gpu is null ? null : new[] { gpu }, gpuUtilization);

    public static string FormatGpuCaption(
        IReadOnlyList<GpuSnapshot>? gpus,
        double gpuUtilization = DefaultGpuUtilization,
        string? cudaVisibleDevices = null,
        string? gpuSplitGb = null)
    {
        if (gpus is null || gpus.Count == 0 || gpus.All(g => g.IsMock || g.MemoryTotalMb < 256))
        {
            return "GPU VRAM could not be read (nvidia-smi unavailable). Fit stays Unknown until a real GPU is detected. Models are not filtered or auto-selected.";
        }

        var real = gpus.Where(g => !g.IsMock && g.MemoryTotalMb >= 256).ToList();
        TryUsableGb(real, gpuUtilization, gpuSplitGb, out var totalGb, out var usableGb);
        var util = ClampUtilization(gpuUtilization);
        var names = string.Join(" + ", real.Select(g => $"{g.Name} {Gb(g.MemoryTotalMb / 1024d)} GB"));
        var cvd = string.IsNullOrWhiteSpace(cudaVisibleDevices) ? "all" : cudaVisibleDevices.Trim();
        if (real.Count == 1)
        {
            return $"This GPU: {real[0].Name} · {Gb(totalGb)} GB total ({Gb(usableGb)} GB usable at {util:P0}). Fit is an estimate from weights + runtime + KV cache against total VRAM — it does not pick a model for you.";
        }

        return $"{real.Count} GPUs (CUDA_VISIBLE_DEVICES={cvd}): {names} · {Gb(totalGb)} GB combined ({Gb(usableGb)} GB usable at {util:P0}). Fit uses the visible set, not only the display GPU.";
    }

    private static bool TryUsableGb(
        IReadOnlyList<GpuSnapshot>? gpus,
        double gpuUtilization,
        string? gpuSplitGb,
        out double totalGb,
        out double usableGb)
    {
        totalGb = 0;
        usableGb = 0;
        if (gpus is null || gpus.Count == 0)
        {
            return false;
        }

        var real = gpus.Where(g => !g.IsMock && g.MemoryTotalMb >= 256).ToList();
        if (real.Count == 0)
        {
            return false;
        }

        totalGb = real.Sum(g => g.MemoryTotalMb) / 1024d;
        var util = ClampUtilization(gpuUtilization);
        if (TryParseSplitGb(gpuSplitGb, real.Count, out var split))
        {
            usableGb = split.Sum();
        }
        else
        {
            usableGb = totalGb * util;
        }

        return usableGb > 0;
    }

    private static bool TryParseSplitGb(string? raw, int deviceCount, out double[] values)
    {
        values = [];
        if (string.IsNullOrWhiteSpace(raw) || deviceCount < 1)
        {
            return false;
        }

        var parts = raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length != deviceCount)
        {
            return false;
        }

        values = new double[parts.Length];
        for (var i = 0; i < parts.Length; i++)
        {
            if (!double.TryParse(parts[i], NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v <= 0)
            {
                return false;
            }

            values[i] = v;
        }

        return true;
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
