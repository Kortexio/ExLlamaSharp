using ExLlamaSharp.Engine.Worker;

namespace ExLlamaSharp.Server.Tests;

public sealed class CudaDeviceEnvironmentTests
{
    [Fact]
    public void Remap_orders_highest_vram_first()
    {
        var inv = new[]
        {
            new CudaGpuInfo { Index = 0, Name = "small", MemoryTotalMiB = 6144, ComputeCapability = 8.6 },
            new CudaGpuInfo { Index = 1, Name = "large", MemoryTotalMiB = 12288, ComputeCapability = 8.6 },
        };

        Assert.Equal("1,0", CudaDeviceEnvironment.RemapStrongestFirst([0, 1], inv));
    }

    [Fact]
    public void Remap_ties_break_on_compute_then_pci()
    {
        var inv = new[]
        {
            new CudaGpuInfo { Index = 0, MemoryTotalMiB = 12288, ComputeCapability = 8.6 },
            new CudaGpuInfo { Index = 1, MemoryTotalMiB = 12288, ComputeCapability = 8.9 },
        };

        Assert.Equal("1,0", CudaDeviceEnvironment.RemapStrongestFirst([0, 1], inv));
    }

    [Fact]
    public void Remap_three_devices_no_sku_assumptions()
    {
        var inv = new[]
        {
            new CudaGpuInfo { Index = 0, MemoryTotalMiB = 8192 },
            new CudaGpuInfo { Index = 1, MemoryTotalMiB = 24576 },
            new CudaGpuInfo { Index = 2, MemoryTotalMiB = 8192 },
        };

        Assert.Equal("1,0,2", CudaDeviceEnvironment.RemapStrongestFirst([0, 1, 2], inv));
    }
}
