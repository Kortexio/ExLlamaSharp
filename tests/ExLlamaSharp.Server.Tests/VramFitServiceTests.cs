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
}
