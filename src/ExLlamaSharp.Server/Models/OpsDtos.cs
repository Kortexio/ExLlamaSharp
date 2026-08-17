using System.Text.Json.Serialization;

namespace ExLlamaSharp.Server.Models;

public sealed class AboutResponse
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = "ExLlamaSharp";

    [JsonPropertyName("version")]
    public string Version { get; init; } = string.Empty;

    [JsonPropertyName("build")]
    public string? Build { get; init; }

    [JsonPropertyName("runtime")]
    public string? Runtime { get; init; }

    [JsonPropertyName("os")]
    public string? Os { get; init; }

    [JsonPropertyName("engine")]
    public string? Engine { get; init; }

    [JsonPropertyName("is_mock")]
    public bool IsMock { get; init; }

    [JsonPropertyName("cuda")]
    public string? Cuda { get; init; }

    [JsonPropertyName("gpus")]
    public List<GpuInfoDto>? Gpus { get; init; }
}

public sealed class GpuInfoDto
{
    [JsonPropertyName("index")]
    public int Index { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("vram_total_mb")]
    public long? VramTotalMb { get; init; }

    [JsonPropertyName("vram_free_mb")]
    public long? VramFreeMb { get; init; }
}

public sealed class HealthResponse
{
    [JsonPropertyName("status")]
    public string Status { get; init; } = "ok";

    [JsonPropertyName("database")]
    public string? Database { get; init; }

    [JsonPropertyName("engine")]
    public string? Engine { get; init; }

    [JsonPropertyName("inference")]
    public string? Inference { get; init; }

    [JsonPropertyName("disk")]
    public string? Disk { get; init; }

    [JsonPropertyName("details")]
    public Dictionary<string, object?>? Details { get; init; }
}

public sealed class ReadyResponse
{
    [JsonPropertyName("ready")]
    public bool Ready { get; init; }

    [JsonPropertyName("model_loaded")]
    public bool ModelLoaded { get; init; }

    [JsonPropertyName("engine_running")]
    public bool EngineRunning { get; init; }
}

public sealed class MetricsJsonResponse
{
    [JsonPropertyName("total_prompt_tokens")]
    public long TotalPromptTokens { get; init; }

    [JsonPropertyName("total_generated_tokens")]
    public long TotalGeneratedTokens { get; init; }

    [JsonPropertyName("num_jobs_waiting")]
    public long NumJobsWaiting { get; init; }

    [JsonPropertyName("num_jobs_running")]
    public long NumJobsRunning { get; init; }

    [JsonPropertyName("num_jobs_swapped")]
    public long NumJobsSwapped { get; init; }

    [JsonPropertyName("num_jobs_finished")]
    public long NumJobsFinished { get; init; }

    [JsonPropertyName("num_pages_used")]
    public long NumPagesUsed { get; init; }

    [JsonPropertyName("num_pages_free")]
    public long NumPagesFree { get; init; }

    [JsonPropertyName("tokens_per_second")]
    public double TokensPerSecond { get; init; }

    [JsonPropertyName("last_step_ms")]
    public double LastStepMs { get; init; }

    [JsonPropertyName("step_count")]
    public long StepCount { get; init; }

    [JsonPropertyName("is_mock")]
    public bool IsMock { get; init; }
}

public sealed class BackupRequest
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

public sealed class BackupResponse
{
    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("size_bytes")]
    public long? SizeBytes { get; init; }
}

public sealed class RestoreRequest
{
    [JsonPropertyName("path")]
    public string Path { get; set; } = string.Empty;
}

public sealed class ModerationRuleRequest
{
    [JsonPropertyName("pattern")]
    public string Pattern { get; set; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; set; } = "block";

    [JsonPropertyName("category")]
    public string Category { get; set; } = "offensive";

    [JsonPropertyName("enabled")]
    public bool Enabled { get; set; } = true;
}

public sealed class ModerationRuleDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("pattern")]
    public string Pattern { get; init; } = string.Empty;

    [JsonPropertyName("action")]
    public string Action { get; init; } = string.Empty;

    [JsonPropertyName("category")]
    public string Category { get; init; } = string.Empty;

    [JsonPropertyName("enabled")]
    public bool Enabled { get; init; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; }
}

public sealed class StubListResponse
{
    [JsonPropertyName("object")]
    public string Object { get; init; } = "list";

    [JsonPropertyName("data")]
    public List<object> Data { get; init; } = [];

    [JsonPropertyName("message")]
    public string? Message { get; init; }
}
