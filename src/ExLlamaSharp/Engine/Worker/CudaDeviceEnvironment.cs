using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace ExLlamaSharp.Engine.Worker;

/// <summary>
/// Sanitizes and remaps CUDA_VISIBLE_DEVICES so cuda:0 is the highest-VRAM GPU
/// among the requested PCI indices. Always sets CUDA_DEVICE_ORDER=PCI_BUS_ID.
/// </summary>
public static class CudaDeviceEnvironment
{
    public static string? Normalize(string? requested, out string? warning)
    {
        warning = null;
        var inventory = QueryGpus();
        var gpuCount = inventory.Count;
        var raw = requested?.Trim();

        List<int> ids;
        if (string.IsNullOrWhiteSpace(raw))
        {
            ids = inventory.Select(g => g.Index).ToList();
            if (ids.Count == 0)
            {
                return null;
            }
        }
        else
        {
            ids = [];
            foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (!int.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) || id < 0)
                {
                    warning = $"Ignoring invalid CUDA_VISIBLE_DEVICES entry '{part}'.";
                    continue;
                }

                ids.Add(id);
            }
        }

        if (ids.Count == 0)
        {
            return gpuCount > 0 ? "0" : null;
        }

        if (gpuCount <= 0)
        {
            return string.Join(",", ids.Distinct());
        }

        var valid = ids.Where(id => id < gpuCount).Distinct().ToList();
        if (valid.Count == 0)
        {
            warning =
                $"CUDA_VISIBLE_DEVICES={raw} is out of range (this machine has {gpuCount} GPU(s), indices 0..{gpuCount - 1}). Using GPU 0.";
            return RemapStrongestFirst([0], inventory);
        }

        return RemapStrongestFirst(valid, inventory);
    }

    public static string RemapStrongestFirst(IReadOnlyList<int> pciIds, IReadOnlyList<CudaGpuInfo> inventory)
    {
        var byIndex = inventory.ToDictionary(g => g.Index);
        var ordered = pciIds
            .Distinct()
            .OrderByDescending(id => byIndex.TryGetValue(id, out var g) ? g.MemoryTotalMiB : 0)
            .ThenByDescending(id => byIndex.TryGetValue(id, out var g) ? g.ComputeCapability : 0)
            .ThenBy(id => id)
            .ToList();
        return string.Join(",", ordered);
    }

    public static void ApplyToProcess(ProcessStartInfo psi, string? requested, ILogger? logger = null)
    {
        var normalized = Normalize(requested, out var warning);
        if (!string.IsNullOrWhiteSpace(warning))
        {
            logger?.LogWarning("{Warning}", warning);
        }

        psi.Environment.Remove("CUDA_VISIBLE_DEVICES");
        psi.Environment.Remove("CUDA_HOME");
        psi.Environment.Remove("CUDA_PATH");
        psi.Environment["CUDA_DEVICE_ORDER"] = "PCI_BUS_ID";

        if (!string.IsNullOrWhiteSpace(normalized))
        {
            psi.Environment["CUDA_VISIBLE_DEVICES"] = normalized;
            logger?.LogInformation("Worker CUDA_VISIBLE_DEVICES={Devices} (strongest-first, PCI_BUS_ID)", normalized);
        }
    }

    public static IReadOnlyList<CudaGpuInfo> QueryGpus()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments =
                    "--query-gpu=index,name,memory.total,memory.used,uuid,compute_cap,display_active --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return [];
            }

            if (!p.WaitForExit(4000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return [];
            }

            var output = p.StandardOutput.ReadToEnd();
            var list = new List<CudaGpuInfo>();
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                var parts = line.Split(',', StringSplitOptions.TrimEntries);
                if (parts.Length < 3 || !int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                {
                    continue;
                }

                double.TryParse(parts.ElementAtOrDefault(2), NumberStyles.Float, CultureInfo.InvariantCulture, out var total);
                double.TryParse(parts.ElementAtOrDefault(3), NumberStyles.Float, CultureInfo.InvariantCulture, out var used);
                var uuid = parts.ElementAtOrDefault(4) ?? "";
                double.TryParse(parts.ElementAtOrDefault(5), NumberStyles.Float, CultureInfo.InvariantCulture, out var cc);
                var display = parts.ElementAtOrDefault(6);
                bool? displayActive = display is null
                    ? null
                    : display.StartsWith("Enabled", StringComparison.OrdinalIgnoreCase);

                list.Add(new CudaGpuInfo
                {
                    Index = idx,
                    Name = parts.ElementAtOrDefault(1) ?? "",
                    MemoryTotalMiB = total,
                    MemoryUsedMiB = used,
                    Uuid = uuid,
                    ComputeCapability = cc,
                    DisplayActive = displayActive,
                });
            }

            return list;
        }
        catch
        {
            return [];
        }
    }

    public static int TryCountGpus() => QueryGpus().Count;
}

public sealed class CudaGpuInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public double MemoryTotalMiB { get; init; }
    public double MemoryUsedMiB { get; init; }
    public string Uuid { get; init; } = "";
    public double ComputeCapability { get; init; }
    public bool? DisplayActive { get; init; }
}
