using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ExLlamaSharp.Engine.Worker;

/// <summary>
/// Sanitizes CUDA_VISIBLE_DEVICES for the EXL3 worker. A value like "1" on a
/// single-GPU box hides the only device and torch.cuda.is_available() becomes false.
/// </summary>
public static class CudaDeviceEnvironment
{
    public static string? Normalize(string? requested, out string? warning)
    {
        warning = null;
        var gpuCount = TryCountGpus();
        var raw = requested?.Trim();
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        var ids = new List<int>();
        foreach (var part in raw.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!int.TryParse(part, out var id) || id < 0)
            {
                warning = $"Ignoring invalid CUDA_VISIBLE_DEVICES entry '{part}'.";
                continue;
            }

            ids.Add(id);
        }

        if (gpuCount <= 0)
        {
            return ids.Count == 0 ? null : string.Join(",", ids);
        }

        var valid = ids.Where(id => id < gpuCount).Distinct().ToList();
        if (valid.Count > 0)
        {
            return string.Join(",", valid);
        }

        warning =
            $"CUDA_VISIBLE_DEVICES={raw} is out of range (this machine has {gpuCount} GPU(s), indices 0..{gpuCount - 1}). Using GPU 0.";
        return "0";
    }

    public static void ApplyToProcess(ProcessStartInfo psi, string? requested, ILogger? logger = null)
    {
        var normalized = Normalize(requested, out var warning);
        if (!string.IsNullOrWhiteSpace(warning))
        {
            logger?.LogWarning("{Warning}", warning);
        }

        psi.Environment.Remove("CUDA_VISIBLE_DEVICES");
        // Torch wheels ship their own CUDA runtime. A host CUDA_HOME (e.g. toolkit 13.3)
        // makes cpp_extension report "No CUDA runtime is found" and can hide the GPU.
        psi.Environment.Remove("CUDA_HOME");
        psi.Environment.Remove("CUDA_PATH");

        if (!string.IsNullOrWhiteSpace(normalized))
        {
            psi.Environment["CUDA_VISIBLE_DEVICES"] = normalized;
        }
    }

    public static int TryCountGpus()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=index --format=csv,noheader",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return 0;
            }

            if (!p.WaitForExit(4000))
            {
                try { p.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return 0;
            }

            var output = p.StandardOutput.ReadToEnd();
            var count = 0;
            foreach (var line in output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                if (int.TryParse(line, out _))
                {
                    count++;
                }
            }

            return count;
        }
        catch
        {
            return 0;
        }
    }
}
