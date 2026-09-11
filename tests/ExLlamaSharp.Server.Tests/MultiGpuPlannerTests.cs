using ExLlamaSharp.Server.Data.Entities;
using ExLlamaSharp.Server.Services;

namespace ExLlamaSharp.Server.Tests;

public sealed class MultiGpuPlannerTests
{
    private readonly MultiGpuPlanner _planner = new();

    [Fact]
    public void Accepts_tensor_with_two_devices()
    {
        var plan = _planner.BuildPlan("0,1", "tensor", 0.85);
        Assert.Equal(new[] { 0, 1 }, plan.DeviceIds);
        Assert.True(plan.IsMultiGpu);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal(ParallelismKind.Pipeline, plan.Kind);
            Assert.Equal("pipeline", plan.AppliedMode);
            Assert.Contains("Windows", plan.CoercionNote);
        }
        else
        {
            Assert.Equal(ParallelismKind.Tensor, plan.Kind);
            Assert.Null(plan.CoercionNote);
        }
    }

    [Fact]
    public void Accepts_pipeline_with_three_devices()
    {
        var plan = _planner.BuildPlan("0,1,2", "pipeline");
        Assert.Equal(ParallelismKind.Pipeline, plan.Kind);
        Assert.Equal(3, plan.NumDevices);
    }

    [Fact]
    public void Rejects_tensor_with_one_device()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => _planner.BuildPlan("0", "tensor"));
        Assert.Contains("at least 2", ex.Message);
    }

    [Fact]
    public void Rejects_model_mode()
    {
        Assert.Throws<InvalidOperationException>(() => _planner.BuildPlan("0,1", "model"));
    }

    [Fact]
    public void Parses_asymmetric_and_symmetric_split()
    {
        var a = _planner.BuildPlan(new AppSettings
        {
            CudaVisibleDevices = "0,1",
            ParallelismMode = "tensor",
            GpuMemoryUtilization = 0.9,
            GpuSplitGb = "10,4.5",
        });
        Assert.Equal(new[] { 10.0, 4.5 }, a.UsePerDeviceGb);

        var b = _planner.BuildPlan(new AppSettings
        {
            CudaVisibleDevices = "0,1",
            ParallelismMode = "tensor",
            GpuMemoryUtilization = 0.9,
            GpuSplitGb = "9.5,9.5",
        });
        Assert.Equal(new[] { 9.5, 9.5 }, b.UsePerDeviceGb);
    }

    [Fact]
    public void Rejects_split_length_mismatch()
    {
        Assert.Throws<InvalidOperationException>(() => _planner.BuildPlan(new AppSettings
        {
            CudaVisibleDevices = "0,1",
            ParallelismMode = "tensor",
            GpuMemoryUtilization = 0.9,
            GpuSplitGb = "10",
        }));
    }

    [Fact]
    public void None_allows_single_device()
    {
        var plan = _planner.BuildPlan("0", "none");
        Assert.Equal(ParallelismKind.None, plan.Kind);
        Assert.False(plan.IsMultiGpu);
    }

    [Fact]
    public void Aligns_cache_tokens_to_256()
    {
        Assert.Equal(10240, MultiGpuPlanner.AlignCacheTokens(10240));
        Assert.Equal(10240, MultiGpuPlanner.AlignCacheTokens(10250));
        Assert.Equal(256, MultiGpuPlanner.AlignCacheTokens(100));
    }
}
