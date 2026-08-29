using System.Text.Json.Serialization;

namespace ExLlamaSharp.Server.Models;

public sealed class ModelLibraryRegisterRequest
{
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("alias")]
    public string? Alias { get; set; }

    [JsonPropertyName("scan")]
    public bool Scan { get; set; }
}

public sealed class ModelLoadRequest
{
    [JsonPropertyName("model_id")]
    public Guid? ModelId { get; set; }

    [JsonPropertyName("alias")]
    public string? Alias { get; set; }

    [JsonPropertyName("path")]
    public string? Path { get; set; }
}

public sealed class ModelUnloadRequest
{
    [JsonPropertyName("model_id")]
    public Guid? ModelId { get; set; }
}

public sealed class ModelPullRequest
{
    [JsonPropertyName("repo_id")]
    public string RepoId { get; set; } = string.Empty;

    [JsonPropertyName("branch")]
    public string? Branch { get; set; }

    [JsonPropertyName("quantize")]
    public bool? Quantize { get; set; }

    [JsonPropertyName("bits")]
    public int? Bits { get; set; }
}

public sealed class ModelQuantizeRequest
{
    [JsonPropertyName("model_id")]
    public Guid? ModelId { get; set; }

    /// <summary>Optional local folder; resolved to a library record when model_id is omitted.</summary>
    [JsonPropertyName("source_path")]
    public string? SourcePath { get; set; }

    [JsonPropertyName("bits")]
    public double? Bits { get; set; }

    [JsonPropertyName("calibration_data")]
    public string? CalibrationData { get; set; }
}

public sealed class ModelImportRequest
{
    [JsonPropertyName("source_path")]
    public string SourcePath { get; set; } = string.Empty;

    [JsonPropertyName("target_format")]
    public string TargetFormat { get; set; } = "exl3";

    [JsonPropertyName("bits")]
    public int? Bits { get; set; }

    [JsonPropertyName("alias")]
    public string? Alias { get; set; }
}

public sealed class ModelAliasRequest
{
    [JsonPropertyName("model_id")]
    public Guid ModelId { get; set; }

    [JsonPropertyName("alias")]
    public string Alias { get; set; } = string.Empty;
}

public sealed class ModelfileDto
{
    [JsonPropertyName("content")]
    public string Content { get; set; } = string.Empty;

    [JsonPropertyName("json")]
    public string? Json { get; set; }
}

public sealed class JobDto
{
    [JsonPropertyName("job_id")]
    public Guid JobId { get; init; }

    [JsonPropertyName("type")]
    public string Type { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("progress_pct")]
    public double ProgressPct { get; init; }

    [JsonPropertyName("eta_seconds")]
    public int? EtaSeconds { get; init; }

    [JsonPropertyName("error")]
    public string? Error { get; init; }

    [JsonPropertyName("parameter_label")]
    public string? ParameterLabel { get; init; }

    [JsonPropertyName("bytes_downloaded")]
    public long? BytesDownloaded { get; init; }

    [JsonPropertyName("bytes_total")]
    public long? BytesTotal { get; init; }

    [JsonPropertyName("size_label")]
    public string? SizeLabel { get; init; }

    [JsonPropertyName("model_id")]
    public Guid? ModelId { get; init; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("updated_at")]
    public DateTime UpdatedAt { get; init; }
}

public sealed class JobCreatedResponse
{
    [JsonPropertyName("job_id")]
    public Guid JobId { get; init; }
}

public sealed class ModelRecordDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("alias")]
    public string? Alias { get; init; }

    [JsonPropertyName("architecture")]
    public string? Architecture { get; init; }

    [JsonPropertyName("size_gb")]
    public double SizeGb { get; init; }

    [JsonPropertyName("context_length")]
    public int ContextLength { get; init; }

    [JsonPropertyName("quant_mode")]
    public string? QuantMode { get; init; }

    [JsonPropertyName("tenant_id")]
    public string TenantId { get; init; } = "default";

    [JsonPropertyName("shared")]
    public bool Shared { get; init; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; }
}

public sealed class ModelLibraryEntryDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("repo_id")]
    public string RepoId { get; init; } = string.Empty;

    [JsonPropertyName("branch")]
    public string? Branch { get; init; }

    [JsonPropertyName("display_name")]
    public string DisplayName { get; init; } = string.Empty;

    [JsonPropertyName("description")]
    public string? Description { get; init; }

    [JsonPropertyName("tags")]
    public string? Tags { get; init; }

    [JsonPropertyName("architecture")]
    public string? Architecture { get; init; }

    [JsonPropertyName("parameters")]
    public string? Parameters { get; init; }

    [JsonPropertyName("context_length")]
    public int ContextLength { get; init; }

    [JsonPropertyName("license")]
    public string? License { get; init; }

    [JsonPropertyName("recommended_bits")]
    public string? RecommendedBits { get; init; }
}
