using System.Diagnostics;
using System.Globalization;

namespace ExLlamaSharp.Server.Services.Ui;

public sealed class GpuInfoService
{
    public async Task<IReadOnlyList<GpuSnapshot>> GetGpusAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments =
                    "--query-gpu=index,name,utilization.gpu,memory.used,memory.total,temperature.gpu --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return MockGpus();
            }

            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutCts.CancelAfter(TimeSpan.FromSeconds(4));
            try
            {
                var output = await process.StandardOutput.ReadToEndAsync(timeoutCts.Token).ConfigureAwait(false);
                await process.WaitForExitAsync(timeoutCts.Token).ConfigureAwait(false);

                if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
                {
                    return MockGpus();
                }

                var list = new List<GpuSnapshot>();
                foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
                {
                    var parts = line.Split(',', StringSplitOptions.TrimEntries);
                    if (parts.Length < 6)
                    {
                        continue;
                    }

                    list.Add(new GpuSnapshot
                    {
                        Index = int.TryParse(parts[0], out var idx) ? idx : list.Count,
                        Name = parts[1],
                        UtilizationPct = ParseDouble(parts[2]),
                        MemoryUsedMb = ParseDouble(parts[3]),
                        MemoryTotalMb = ParseDouble(parts[4]),
                        TemperatureC = ParseDouble(parts[5]),
                        IsMock = false,
                    });
                }

                return list.Count > 0 ? list : MockGpus();
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                return MockGpus();
            }
        }
        catch
        {
            return MockGpus();
        }
    }

    public async Task<GpuSnapshot?> GetPrimaryAsync(CancellationToken cancellationToken = default)
    {
        var gpus = await GetGpusAsync(cancellationToken).ConfigureAwait(false);
        return gpus.FirstOrDefault();
    }

    private static double ParseDouble(string value) =>
        double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : 0;

    private static IReadOnlyList<GpuSnapshot> MockGpus() =>
    [
        new GpuSnapshot
        {
            Index = 0,
            Name = "Mock GPU (nvidia-smi unavailable)",
            UtilizationPct = 12,
            MemoryUsedMb = 2048,
            MemoryTotalMb = 24576,
            TemperatureC = 42,
            IsMock = true,
        },
    ];
}

public sealed class GpuSnapshot
{
    public int Index { get; init; }
    public string Name { get; init; } = string.Empty;
    public double UtilizationPct { get; init; }
    public double MemoryUsedMb { get; init; }
    public double MemoryTotalMb { get; init; }
    public double TemperatureC { get; init; }
    public bool IsMock { get; init; }

    public double MemoryPct => MemoryTotalMb <= 0 ? 0 : MemoryUsedMb / MemoryTotalMb * 100;
}
