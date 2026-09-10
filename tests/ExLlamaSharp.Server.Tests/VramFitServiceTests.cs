using ExLlamaSharp.Server.Services.Ui;

namespace ExLlamaSharp.Server.Tests;

public class VramFitServiceTests
{
    private readonly VramFitService _fit = new();

    private static GpuSnapshot Gpu(double totalMb, bool mock = false) => new()
    {
        Name = "Test GPU",
        MemoryTotalMb = totalMb,
        IsMock = mock,
    };

    [Fact]
    public void Pending_size_is_ellipsis_not_a_pick()
    {
        var result = _fit.Evaluate(null, Gpu(12288));
        Assert.Equal(VramFitKind.Pending, result.Kind);
        Assert.Equal("…", result.Label);
    }

    [Fact]
    public void Mock_gpu_is_unknown_even_with_size()
    {
        var result = _fit.EvaluateGb(4, Gpu(24576, mock: true));
        Assert.Equal(VramFitKind.UnknownGpu, result.Kind);
        Assert.Equal("Unknown", result.Label);
    }

    [Fact]
    public void Missing_size_is_unknown()
    {
        var result = _fit.Evaluate(0, Gpu(12288));
        Assert.Equal(VramFitKind.UnknownSize, result.Kind);
    }

    [Theory]
    [InlineData(4.0, VramFitKind.Fits)]
    [InlineData(7.5, VramFitKind.Tight)]
    [InlineData(12.0, VramFitKind.TooLarge)]
    public void Twelve_gb_card_classifies_by_weight_size(double weightGb, VramFitKind expected)
    {
        // 12 GB × 90% usable = 10.8 GB. Heuristic: weights + 1.25 GB + ~12% KV.
        var result = _fit.EvaluateGb(weightGb, Gpu(12 * 1024), 0.90);
        Assert.Equal(expected, result.Kind);
    }

    [Fact]
    public void Does_not_hide_too_large_models()
    {
        var result = _fit.EvaluateGb(40, Gpu(8192));
        Assert.Equal(VramFitKind.TooLarge, result.Kind);
        Assert.Equal("Too large", result.Label);
        Assert.Contains("estimated", result.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Combined_visible_gpus_are_not_judged_by_the_display_card_alone()
    {
        var gpus = new[]
        {
            new GpuSnapshot { Index = 0, Name = "RTX 3050", MemoryTotalMb = 8192 },
            new GpuSnapshot { Index = 1, Name = "RTX 3060", MemoryTotalMb = 12288 },
        };
        var onDisplayOnly = _fit.EvaluateGb(10, gpus[0], 0.90);
        var onBoth = _fit.EvaluateGb(10, gpus, 0.90, "10.8,6.2");
        Assert.Equal(VramFitKind.TooLarge, onDisplayOnly.Kind);
        Assert.Equal(VramFitKind.Fits, onBoth.Kind);
        Assert.Contains("visible GPUs", onBoth.Detail, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void FilterVisible_keeps_requested_pci_indices_strongest_first()
    {
        var gpus = new[]
        {
            new GpuSnapshot { Index = 0, Name = "RTX 3050", MemoryTotalMb = 8192 },
            new GpuSnapshot { Index = 1, Name = "RTX 3060", MemoryTotalMb = 12288 },
        };
        var visible = GpuInfoService.FilterVisible(gpus, "0,1");
        Assert.Equal(2, visible.Count);
        Assert.Equal(1, visible[0].Index);
        Assert.Equal(0, visible[1].Index);
    }
}
