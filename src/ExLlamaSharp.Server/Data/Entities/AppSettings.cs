namespace ExLlamaSharp.Server.Data.Entities;

/// <summary>Single-row application settings (Id always 1).</summary>
public sealed class AppSettings
{
    public int Id { get; set; } = 1;

    public string BindAddress { get; set; } = "127.0.0.1";
    public int Port { get; set; } = 14563;
    public string Cors { get; set; } = "*";
    public string? TlsCertPath { get; set; }

    public int MaxNumSeqs { get; set; } = 256;
    public int MaxChunkSize { get; set; } = 2048;
    /// <summary>KV / context window at model load (Ollama <c>num_ctx</c>).</summary>
    public int MaxBatchedTokens { get; set; } = 8192;
    /// <summary>Default max tokens to generate when the request omits max_tokens (Ollama <c>num_predict</c>).</summary>
    public int DefaultMaxTokens { get; set; } = 2048;
    public double GpuMemoryUtilization { get; set; } = 0.90;
    public int RequestTimeoutSeconds { get; set; } = 300;

    public bool LoadModelOnStartup { get; set; }
    public Guid? LastLoadedModelId { get; set; }
    /// <summary>disabled | daily | weekly</summary>
    public string AutoBackupSchedule { get; set; } = "disabled";

    public string? WebhookUrl { get; set; }
    public string? WebhookSecret { get; set; }

    public bool ContentModerationEnabled { get; set; }
    public bool MultiTenancyEnabled { get; set; }
    public bool ShowAdvancedMetrics { get; set; }

    public string CudaVisibleDevices { get; set; } = "0";
    /// <summary>none | tensor | pipeline</summary>
    public string ParallelismMode { get; set; } = "none";
    /// <summary>Optional GB per visible GPU after remap (CSV). Empty = auto from VRAM × util.</summary>
    public string? GpuSplitGb { get; set; }

    public bool SpeculativeEnabled { get; set; }
    public Guid? DraftModelId { get; set; }
    public int DraftK { get; set; } = 5;

    public string ModelsPath { get; set; } = string.Empty;

    /// <summary>USD (or local currency) per 1M tokens for Usage estimated cost.</summary>
    public decimal EstimatedCostPerMillionTokens { get; set; }
}
