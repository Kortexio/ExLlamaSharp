using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ExLlamaSharp.Engine;

namespace ExLlamaSharp.Server.Services;

public sealed class AboutService
{
    private readonly EngineHostService _engineHost;

    public AboutService(EngineHostService engineHost)
    {
        _engineHost = engineHost;
    }

    public AboutInfo GetAbout()
    {
        var asm = Assembly.GetExecutingAssembly();
        var version = asm.GetName().Version?.ToString() ?? "0.0.0";
        var infoVersion = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;

        EngineMetrics? metrics = null;
        try
        {
            if (_engineHost.IsLoaded)
            {
                metrics = _engineHost.Engine.GetMetrics();
            }
        }
        catch
        {
            // best-effort
        }

        return new AboutInfo
        {
            Version = infoVersion ?? version,
            BuildDate = GetBuildDate(asm),
            Runtime = new RuntimeInfo
            {
                Dotnet = Environment.Version.ToString(),
                Os = RuntimeInformation.OSDescription,
                Architecture = RuntimeInformation.OSArchitecture.ToString(),
                ProcessArchitecture = RuntimeInformation.ProcessArchitecture.ToString(),
                FrameworkDescription = RuntimeInformation.FrameworkDescription,
            },
            Engine = new EngineInfo
            {
                Name = _engineHost.Engine.GetType().Name,
                IsMock = _engineHost.Engine.IsMock,
                IsLoaded = _engineHost.IsLoaded,
                IsRunning = _engineHost.IsRunning,
                LoadedModelId = _engineHost.LoadedModelId,
                LoadedModelPath = _engineHost.LoadedModelPath,
                TokensPerSecond = metrics?.TokensPerSecond,
            },
            Gpu = DetectGpu(),
        };
    }

    public string GetAboutJson() =>
        JsonSerializer.Serialize(GetAbout(), new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        });

    private static DateTime? GetBuildDate(Assembly asm)
    {
        try
        {
            var path = asm.Location;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                return File.GetLastWriteTimeUtc(path);
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    private static GpuInfo DetectGpu()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "nvidia-smi",
                Arguments = "--query-gpu=name,memory.total,compute_cap --format=csv,noheader,nounits",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var process = Process.Start(psi);
            if (process is null)
            {
                return new GpuInfo { Available = false };
            }

            var output = process.StandardOutput.ReadToEnd();
            process.WaitForExit(3000);
            if (process.ExitCode != 0 || string.IsNullOrWhiteSpace(output))
            {
                return new GpuInfo { Available = false };
            }

            var line = output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).FirstOrDefault();
            if (line is null)
            {
                return new GpuInfo { Available = false };
            }

            var parts = line.Split(',', StringSplitOptions.TrimEntries);
            return new GpuInfo
            {
                Available = true,
                Name = parts.ElementAtOrDefault(0),
                VramTotalMb = parts.Length > 1 && double.TryParse(parts[1], out var mb) ? mb : null,
                ComputeCapability = parts.ElementAtOrDefault(2),
            };
        }
        catch
        {
            return new GpuInfo { Available = false };
        }
    }
}

public sealed class AboutInfo
{
    public string Version { get; init; } = "0.0.0";
    public DateTime? BuildDate { get; init; }
    public RuntimeInfo Runtime { get; init; } = new();
    public EngineInfo Engine { get; init; } = new();
    public GpuInfo Gpu { get; init; } = new();
}

public sealed class RuntimeInfo
{
    public string Dotnet { get; init; } = string.Empty;
    public string Os { get; init; } = string.Empty;
    public string Architecture { get; init; } = string.Empty;
    public string ProcessArchitecture { get; init; } = string.Empty;
    public string FrameworkDescription { get; init; } = string.Empty;
}

public sealed class EngineInfo
{
    public string Name { get; init; } = "Unknown";
    public bool IsMock { get; init; }
    public bool IsLoaded { get; init; }
    public bool IsRunning { get; init; }
    public Guid? LoadedModelId { get; init; }
    public string? LoadedModelPath { get; init; }
    public double? TokensPerSecond { get; init; }
}

public sealed class GpuInfo
{
    public bool Available { get; init; }
    public string? Name { get; init; }
    public double? VramTotalMb { get; init; }
    public string? ComputeCapability { get; init; }
}
