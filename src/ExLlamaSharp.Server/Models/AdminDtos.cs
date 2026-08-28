using System.Text.Json.Serialization;

namespace ExLlamaSharp.Server.Models;

public sealed class SettingsDto
{
    [JsonPropertyName("bind_address")]
    public string? BindAddress { get; set; }

    [JsonPropertyName("port")]
    public int? Port { get; set; }

    [JsonPropertyName("cors")]
    public string? Cors { get; set; }

    [JsonPropertyName("tls_cert_path")]
    public string? TlsCertPath { get; set; }

    [JsonPropertyName("max_num_seqs")]
    public int? MaxNumSeqs { get; set; }

    [JsonPropertyName("max_chunk_size")]
    public int? MaxChunkSize { get; set; }

    [JsonPropertyName("max_batched_tokens")]
    public int? MaxBatchedTokens { get; set; }

    [JsonPropertyName("gpu_memory_utilization")]
    public double? GpuMemoryUtilization { get; set; }

    [JsonPropertyName("request_timeout_seconds")]
    public int? RequestTimeoutSeconds { get; set; }

    [JsonPropertyName("load_model_on_startup")]
    public bool? LoadModelOnStartup { get; set; }

    [JsonPropertyName("last_loaded_model_id")]
    public Guid? LastLoadedModelId { get; set; }

    [JsonPropertyName("auto_backup_schedule")]
    public string? AutoBackupSchedule { get; set; }

    [JsonPropertyName("webhook_url")]
    public string? WebhookUrl { get; set; }

    [JsonPropertyName("webhook_secret")]
    public string? WebhookSecret { get; set; }

    [JsonPropertyName("content_moderation_enabled")]
    public bool? ContentModerationEnabled { get; set; }

    [JsonPropertyName("multi_tenancy_enabled")]
    public bool? MultiTenancyEnabled { get; set; }

    [JsonPropertyName("show_advanced_metrics")]
    public bool? ShowAdvancedMetrics { get; set; }

    [JsonPropertyName("cuda_visible_devices")]
    public string? CudaVisibleDevices { get; set; }

    [JsonPropertyName("parallelism_mode")]
    public string? ParallelismMode { get; set; }

    [JsonPropertyName("speculative_enabled")]
    public bool? SpeculativeEnabled { get; set; }

    [JsonPropertyName("draft_model_id")]
    public Guid? DraftModelId { get; set; }

    [JsonPropertyName("draft_k")]
    public int? DraftK { get; set; }

    [JsonPropertyName("models_path")]
    public string? ModelsPath { get; set; }
}

public sealed class CreateApiKeyRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("scopes")]
    public string? Scopes { get; set; }

    [JsonPropertyName("rpm")]
    public int? Rpm { get; set; }

    [JsonPropertyName("tpm")]
    public int? Tpm { get; set; }

    [JsonPropertyName("priority")]
    public int? Priority { get; set; }

    [JsonPropertyName("cost_per_million_tokens")]
    public decimal? CostPerMillionTokens { get; set; }

    [JsonPropertyName("expires_at")]
    public DateTime? ExpiresAt { get; set; }

    [JsonPropertyName("user_id")]
    public Guid? UserId { get; set; }

    [JsonPropertyName("tenant_id")]
    public string? TenantId { get; set; }
}

public sealed record ApiKeyDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("key_prefix")]
    public string KeyPrefix { get; init; } = string.Empty;

    /// <summary>Only returned on create.</summary>
    [JsonPropertyName("key")]
    public string? Key { get; init; }

    [JsonPropertyName("scopes")]
    public string Scopes { get; init; } = string.Empty;

    [JsonPropertyName("rpm")]
    public int Rpm { get; init; }

    [JsonPropertyName("tpm")]
    public int Tpm { get; init; }

    [JsonPropertyName("priority")]
    public int Priority { get; init; }

    [JsonPropertyName("cost_per_million_tokens")]
    public decimal CostPerMillionTokens { get; init; }

    [JsonPropertyName("expires_at")]
    public DateTime? ExpiresAt { get; init; }

    [JsonPropertyName("revoked")]
    public bool Revoked { get; init; }

    [JsonPropertyName("tenant_id")]
    public string TenantId { get; init; } = "default";

    [JsonPropertyName("user_id")]
    public Guid? UserId { get; init; }
}

public sealed class CreateUserRequest
{
    [JsonPropertyName("username")]
    public string Username { get; set; } = string.Empty;

    [JsonPropertyName("password")]
    public string Password { get; set; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; set; } = "viewer";

    [JsonPropertyName("tenant_id")]
    public string? TenantId { get; set; }
}

public sealed class PatchUserRequest
{
    [JsonPropertyName("password")]
    public string? Password { get; set; }

    [JsonPropertyName("role")]
    public string? Role { get; set; }

    [JsonPropertyName("tenant_id")]
    public string? TenantId { get; set; }
}

public sealed class UserDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("username")]
    public string Username { get; init; } = string.Empty;

    [JsonPropertyName("role")]
    public string Role { get; init; } = string.Empty;

    [JsonPropertyName("tenant_id")]
    public string TenantId { get; init; } = "default";

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; }

    [JsonPropertyName("last_active")]
    public DateTime? LastActive { get; init; }
}

public sealed class CreateAbTestRequest
{
    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("model_a_id")]
    public Guid ModelAId { get; set; }

    [JsonPropertyName("model_b_id")]
    public Guid ModelBId { get; set; }

    /// <summary>Fraction routed to model A (0–1). Default 0.5.</summary>
    [JsonPropertyName("split_ratio")]
    public double? SplitRatio { get; set; }

    [JsonPropertyName("active")]
    public bool? Active { get; set; }

    [JsonPropertyName("tenant_id")]
    public string? TenantId { get; set; }
}

public sealed class AbVoteRequest
{
    [JsonPropertyName("ab_test_id")]
    public Guid AbTestId { get; set; }

    /// <summary>Stable id for consistent assignment (defaults to a new guid).</summary>
    [JsonPropertyName("request_id")]
    public string? RequestId { get; set; }

    [JsonPropertyName("preferred")]
    public string? Preferred { get; set; }
}

public sealed record AbTestDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("model_a_id")]
    public Guid ModelAId { get; init; }

    [JsonPropertyName("model_b_id")]
    public Guid ModelBId { get; init; }

    [JsonPropertyName("split_ratio")]
    public double SplitRatio { get; init; }

    [JsonPropertyName("active")]
    public bool Active { get; init; }

    [JsonPropertyName("tenant_id")]
    public string TenantId { get; init; } = "default";

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; }
}

public sealed class CreateTenantRequest
{
    [JsonPropertyName("id")]
    public string Id { get; set; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    [JsonPropertyName("subdomain")]
    public string? Subdomain { get; set; }

    [JsonPropertyName("active")]
    public bool? Active { get; set; }
}

public sealed record TenantDto
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("subdomain")]
    public string? Subdomain { get; init; }

    [JsonPropertyName("active")]
    public bool Active { get; init; }

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; }
}

public sealed class CreateAdapterRequest
{
    [JsonPropertyName("base_model_id")]
    public Guid BaseModelId { get; set; }

    [JsonPropertyName("name")]
    public string Name { get; set; } = string.Empty;

    /// <summary>Local path to adapter weights (registered for metadata; not applied at inference yet).</summary>
    [JsonPropertyName("path")]
    public string? Path { get; set; }

    [JsonPropertyName("rank")]
    public int? Rank { get; set; }

    [JsonPropertyName("alpha")]
    public double? Alpha { get; set; }

    [JsonPropertyName("tenant_id")]
    public string? TenantId { get; set; }
}

public sealed record AdapterDto
{
    [JsonPropertyName("id")]
    public Guid Id { get; init; }

    [JsonPropertyName("base_model_id")]
    public Guid BaseModelId { get; init; }

    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("path")]
    public string Path { get; init; } = string.Empty;

    [JsonPropertyName("rank")]
    public int Rank { get; init; }

    [JsonPropertyName("alpha")]
    public double Alpha { get; init; }

    [JsonPropertyName("tenant_id")]
    public string TenantId { get; init; } = "default";

    [JsonPropertyName("created_at")]
    public DateTime CreatedAt { get; init; }
}
