using System.Reflection;
using System.Runtime.InteropServices;
using System.Text.Json;
using ExLlamaSharp.Engine;
using ExLlamaSharp.Engine.Worker;

namespace ExLlamaSharp.Server.Services;

public sealed class AboutService
{
    private readonly EngineHostService _engineHost;
    private readonly SettingsService _settings;

    public AboutService(EngineHostService engineHost, SettingsService settings)
    {
        _engineHost = engineHost;
        _settings = settings;
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

    private GpuInfo DetectGpu()
    {
        var gpus = CudaDeviceEnvironment.QueryGpus();
        if (gpus.Count == 0)
        {
            return new GpuInfo { Available = false };
        }

        var remappedCsv = CudaDeviceEnvironment.Normalize(_settings.PeekOrDefault().CudaVisibleDevices, out _);
        var remapped = string.IsNullOrWhiteSpace(remappedCsv)
            ? []
            : remappedCsv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
                .Select(s => int.TryParse(s, out var id) ? id : -1)
                .Where(id => id >= 0)
                .ToList();
        var cudaOf = remapped
            .Select((pci, i) => (pci, i))
            .ToDictionary(x => x.pci, x => x.i);
        var strongest = gpus.OrderByDescending(g => g.MemoryTotalMiB).ThenBy(g => g.Index).First();
        return new GpuInfo
        {
            Available = true,
            Name = strongest.Name,
            VramTotalMb = strongest.MemoryTotalMiB,
            ComputeCapability = strongest.ComputeCapability > 0
                ? strongest.ComputeCapability.ToString("0.0")
                : null,
            Devices = gpus.Select(g => new GpuDeviceInfo
            {
                Index = g.Index,
                Name = g.Name,
                VramTotalMb = g.MemoryTotalMiB,
                VramUsedMb = g.MemoryUsedMiB,
                Uuid = g.Uuid,
                DisplayActive = g.DisplayActive,
                CudaIndex = cudaOf.TryGetValue(g.Index, out var cuda) ? cuda : null,
                InVisibleSet = cudaOf.ContainsKey(g.Index),
            }).ToList(),
        };
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
    public IReadOnlyList<GpuDeviceInfo> Devices { get; init; } = [];
}

public sealed class GpuDeviceInfo
{
    public int Index { get; init; }
    public string Name { get; init; } = "";
    public double VramTotalMb { get; init; }
    public double VramUsedMb { get; init; }
    public string Uuid { get; init; } = "";
    public bool? DisplayActive { get; init; }
    public int? CudaIndex { get; init; }
    public bool InVisibleSet { get; init; }
}
