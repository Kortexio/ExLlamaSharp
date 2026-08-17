using ExLlamaSharp.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace ExLlamaSharp.Server.Data;

public static class DbInitializer
{
    public static async Task InitializeAsync(AppDbContext db, ILogger logger, CancellationToken cancellationToken = default)
    {
        await db.Database.EnsureCreatedAsync(cancellationToken).ConfigureAwait(false);

        await SeedTenantAsync(db, cancellationToken).ConfigureAwait(false);
        await SeedSettingsAsync(db, cancellationToken).ConfigureAwait(false);
        await SeedModelLibraryAsync(db, cancellationToken).ConfigureAwait(false);
        await SeedDevAdminAsync(db, logger, cancellationToken).ConfigureAwait(false);

        await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);
        logger.LogInformation("Database initialized at {Path}", db.Database.GetDbConnection().DataSource);
    }

    private static async Task SeedTenantAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Tenants.AnyAsync(t => t.Id == "default", cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        db.Tenants.Add(new Tenant
        {
            Id = "default",
            Name = "Default",
            Subdomain = null,
            Active = true,
            CreatedAt = DateTime.UtcNow,
            Quota = new TenantQuota
            {
                TenantId = "default",
                MaxUsers = 50,
                MaxKeys = 100,
                MaxModels = 50,
                MaxStorageGb = 1000,
                RequestsPerHour = 100_000,
                TokensPerMonth = 1_000_000_000,
            },
        });
    }

    private static async Task SeedSettingsAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        if (await db.Settings.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        var dataRoot = Environment.GetEnvironmentVariable("EXLLAMASHARP_DATA_ROOT");
        if (string.IsNullOrWhiteSpace(dataRoot))
        {
            dataRoot = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ExLlamaSharp");
        }

        var modelsPath = Path.Combine(dataRoot, "models");
        Directory.CreateDirectory(modelsPath);

        db.Settings.Add(new AppSettings
        {
            Id = 1,
            BindAddress = "127.0.0.1",
            Port = 14563,
            Cors = "*",
            MaxNumSeqs = 256,
            MaxChunkSize = 2048,
            MaxBatchedTokens = 8192,
            GpuMemoryUtilization = 0.90,
            RequestTimeoutSeconds = 300,
            LoadModelOnStartup = false,
            AutoBackupSchedule = "disabled",
            ContentModerationEnabled = false,
            MultiTenancyEnabled = false,
            ShowAdvancedMetrics = false,
            CudaVisibleDevices = "0",
            ParallelismMode = "none",
            SpeculativeEnabled = false,
            DraftK = 5,
            ModelsPath = modelsPath,
        });
    }

    /// <summary>
    /// Seeds a well-known development admin key: <c>sk-exllamasharp-dev</c>.
    /// Only created when no keys exist yet (first run).
    /// </summary>
    private static async Task SeedDevAdminAsync(AppDbContext db, ILogger logger, CancellationToken cancellationToken)
    {
        if (await db.ApiKeys.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        const string rawKey = "sk-exllamasharp-dev";
        var hash = Auth.ApiKeyHasher.Hash(rawKey);

        if (!await db.Users.AnyAsync(u => u.Username == "admin", cancellationToken).ConfigureAwait(false))
        {
            db.Users.Add(new User
            {
                Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
                Username = "admin",
                PasswordHash = Auth.PasswordHasher.HashDeterministic(
                    "changeme",
                    Convert.FromHexString("00000000000000000000000000000001")),
                Role = "admin",
                TenantId = "default",
                CreatedAt = DateTime.UtcNow,
            });
        }

        db.ApiKeys.Add(new ApiKey
        {
            Id = Guid.Parse("00000000-0000-0000-0000-0000000000a1"),
            Name = "Dev Admin",
            KeyHash = hash,
            KeyPrefix = "sk-exll",
            Scopes = "chat,completions,embeddings,admin",
            Rpm = 600,
            Tpm = 1_000_000,
            Priority = 1,
            CostPerMillionTokens = 0,
            Revoked = false,
            TenantId = "default",
            UserId = Guid.Parse("00000000-0000-0000-0000-000000000001"),
        });

        logger.LogWarning(
            "Seeded development API key '{Key}' and admin user admin/changeme. Change before production use.",
            rawKey);
    }

    private static async Task SeedModelLibraryAsync(AppDbContext db, CancellationToken cancellationToken)
    {
        if (await db.ModelLibrary.AnyAsync(cancellationToken).ConfigureAwait(false))
        {
            return;
        }

        db.ModelLibrary.AddRange(
            new ModelLibraryEntry
            {
                Id = "llama-3.2-1b-instruct-exl3",
                RepoId = "turboderp/Llama-3.2-1B-Instruct-exl3",
                Branch = "4.0bpw",
                DisplayName = "Llama 3.2 1B Instruct (EXL3)",
                Description = "Small EXL3 demo model for first-run smoke tests.",
                Tags = "chat,instruct,1b,llama,exl3,demo",
                Architecture = "LlamaForCausalLM",
                Parameters = "1B",
                ContextLength = 128_000,
                License = "llama3.2",
                RecommendedBits = "4.0",
            },
            new ModelLibraryEntry
            {
                Id = "llama-3.1-8b-instruct",
                RepoId = "meta-llama/Meta-Llama-3.1-8B-Instruct",
                Branch = "main",
                DisplayName = "Llama 3.1 8B Instruct",
                Description = "Meta Llama 3.1 8B instruction-tuned model for chat and coding.",
                Tags = "chat,instruct,8b,llama",
                Architecture = "LlamaForCausalLM",
                Parameters = "8B",
                ContextLength = 128_000,
                License = "llama3.1",
                RecommendedBits = "4.0,3.5",
            },
            new ModelLibraryEntry
            {
                Id = "mistral-7b-instruct",
                RepoId = "mistralai/Mistral-7B-Instruct-v0.3",
                Branch = "main",
                DisplayName = "Mistral 7B Instruct",
                Description = "Mistral 7B instruction-tuned model — fast and capable for general chat.",
                Tags = "chat,instruct,7b,mistral",
                Architecture = "MistralForCausalLM",
                Parameters = "7B",
                ContextLength = 32_768,
                License = "apache-2.0",
                RecommendedBits = "4.0,3.5",
            },
            new ModelLibraryEntry
            {
                Id = "phi-4",
                RepoId = "microsoft/phi-4",
                Branch = "main",
                DisplayName = "Phi-4",
                Description = "Microsoft Phi-4 — compact high-quality reasoning model.",
                Tags = "chat,instruct,reasoning,phi",
                Architecture = "Phi3ForCausalLM",
                Parameters = "14B",
                ContextLength = 16_384,
                License = "mit",
                RecommendedBits = "4.0,3.5",
            },
            new ModelLibraryEntry
            {
                Id = "qwen-2.5-7b-instruct",
                RepoId = "Qwen/Qwen2.5-7B-Instruct",
                Branch = "main",
                DisplayName = "Qwen 2.5 7B Instruct",
                Description = "Alibaba Qwen 2.5 7B instruction-tuned with strong multilingual support.",
                Tags = "chat,instruct,7b,qwen,multilingual",
                Architecture = "Qwen2ForCausalLM",
                Parameters = "7B",
                ContextLength = 32_768,
                License = "apache-2.0",
                RecommendedBits = "4.0,3.5",
            });
    }
}
