using ExLlamaSharp.Engine.Worker;

namespace ExLlamaSharp.Server.Services.Ui;

public interface IGpuInventory
{
    IReadOnlyList<GpuSnapshot> QueryGpus();
    IReadOnlyList<GpuSnapshot> QueryVisible(string? cudaVisibleDevices);
}

public sealed class GpuInventoryService : IGpuInventory
{
    public IReadOnlyList<GpuSnapshot> QueryGpus() =>
        CudaDeviceEnvironment.QueryGpus()
            .Select(g => new GpuSnapshot
            {
                Index = g.Index,
                Name = g.Name,
                MemoryTotalMb = g.MemoryTotalMiB,
                MemoryUsedMb = g.MemoryUsedMiB,
                Uuid = g.Uuid,
            })
            .ToList();

    public IReadOnlyList<GpuSnapshot> QueryVisible(string? cudaVisibleDevices) =>
        GpuInfoService.FilterVisible(QueryGpus(), cudaVisibleDevices);
}