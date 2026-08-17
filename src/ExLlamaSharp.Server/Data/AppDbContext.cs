using ExLlamaSharp.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;

namespace ExLlamaSharp.Server.Data;

public sealed class AppDbContext : DbContext
{
    public AppDbContext(DbContextOptions<AppDbContext> options)
        : base(options)
    {
    }

    public DbSet<User> Users => Set<User>();
    public DbSet<ApiKey> ApiKeys => Set<ApiKey>();
    public DbSet<AppSettings> Settings => Set<AppSettings>();
    public DbSet<ModelRecord> Models => Set<ModelRecord>();
    public DbSet<ModelLibraryEntry> ModelLibrary => Set<ModelLibraryEntry>();
    public DbSet<ModelJob> ModelJobs => Set<ModelJob>();
    public DbSet<Conversation> Conversations => Set<Conversation>();
    public DbSet<ConversationMessage> ConversationMessages => Set<ConversationMessage>();
    public DbSet<AuditLog> AuditLogs => Set<AuditLog>();
    public DbSet<Tenant> Tenants => Set<Tenant>();
    public DbSet<TenantQuota> TenantQuotas => Set<TenantQuota>();
    public DbSet<AbTest> AbTests => Set<AbTest>();
    public DbSet<LoraAdapter> LoraAdapters => Set<LoraAdapter>();
    public DbSet<ModerationRule> ModerationRules => Set<ModerationRule>();
    public DbSet<BackupHistory> BackupHistory => Set<BackupHistory>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<User>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => new { x.TenantId, x.Username }).IsUnique();
            e.Property(x => x.Username).HasMaxLength(128).IsRequired();
            e.Property(x => x.Role).HasMaxLength(32).IsRequired();
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId);
        });

        modelBuilder.Entity<ApiKey>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.KeyHash).IsUnique();
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.Name).HasMaxLength(128).IsRequired();
            e.Property(x => x.KeyHash).HasMaxLength(128).IsRequired();
            e.Property(x => x.KeyPrefix).HasMaxLength(16).IsRequired();
            e.Property(x => x.Scopes).HasMaxLength(256).IsRequired();
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.CostPerMillionTokens).HasPrecision(18, 6);
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId);
            e.HasOne(x => x.User).WithMany(u => u.ApiKeys).HasForeignKey(x => x.UserId);
        });

        modelBuilder.Entity<AppSettings>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.BindAddress).HasMaxLength(64).IsRequired();
            e.Property(x => x.Cors).HasMaxLength(512).IsRequired();
            e.Property(x => x.AutoBackupSchedule).HasMaxLength(32).IsRequired();
            e.Property(x => x.CudaVisibleDevices).HasMaxLength(64).IsRequired();
            e.Property(x => x.ParallelismMode).HasMaxLength(32).IsRequired();
            e.Property(x => x.ModelsPath).HasMaxLength(1024).IsRequired();
        });

        modelBuilder.Entity<ModelRecord>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Alias).IsUnique();
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.Path).HasMaxLength(1024).IsRequired();
            e.Property(x => x.Alias).HasMaxLength(128);
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId);
        });

        modelBuilder.Entity<ModelLibraryEntry>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.RepoId);
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.RepoId).HasMaxLength(256).IsRequired();
            e.Property(x => x.DisplayName).HasMaxLength(256).IsRequired();
        });

        modelBuilder.Entity<ModelJob>(e =>
        {
            e.HasKey(x => x.JobId);
            e.HasIndex(x => x.Status);
            e.Property(x => x.Type).HasMaxLength(32).IsRequired();
            e.Property(x => x.Status).HasMaxLength(32).IsRequired();
            e.HasOne(x => x.Model).WithMany().HasForeignKey(x => x.ModelId);
        });

        modelBuilder.Entity<Conversation>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.TenantId);
            e.Property(x => x.Title).HasMaxLength(256).IsRequired();
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.HasOne(x => x.Tenant).WithMany().HasForeignKey(x => x.TenantId);
            e.HasOne(x => x.User).WithMany().HasForeignKey(x => x.UserId);
            e.HasOne(x => x.Model).WithMany().HasForeignKey(x => x.ModelId);
        });

        modelBuilder.Entity<ConversationMessage>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.ConversationId);
            e.Property(x => x.Role).HasMaxLength(32).IsRequired();
            e.HasOne(x => x.Conversation)
                .WithMany(c => c.Messages)
                .HasForeignKey(x => x.ConversationId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<AuditLog>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Timestamp);
            e.HasIndex(x => x.TenantId);
            e.HasIndex(x => x.KeyId);
            e.Property(x => x.Endpoint).HasMaxLength(256).IsRequired();
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.Property(x => x.EstimatedCost).HasPrecision(18, 8);
            e.Property(x => x.ModelVariant).HasMaxLength(8);
        });

        modelBuilder.Entity<Tenant>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Subdomain).IsUnique();
            e.Property(x => x.Id).HasMaxLength(64);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.Subdomain).HasMaxLength(128);
            e.HasOne(x => x.Quota)
                .WithOne(q => q.Tenant)
                .HasForeignKey<TenantQuota>(q => q.TenantId);
        });

        modelBuilder.Entity<TenantQuota>(e =>
        {
            e.HasKey(x => x.TenantId);
            e.Property(x => x.TenantId).HasMaxLength(64);
        });

        modelBuilder.Entity<AbTest>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.HasOne(x => x.ModelA).WithMany().HasForeignKey(x => x.ModelAId).OnDelete(DeleteBehavior.Restrict);
            e.HasOne(x => x.ModelB).WithMany().HasForeignKey(x => x.ModelBId).OnDelete(DeleteBehavior.Restrict);
        });

        modelBuilder.Entity<LoraAdapter>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Name).HasMaxLength(256).IsRequired();
            e.Property(x => x.Path).HasMaxLength(1024).IsRequired();
            e.Property(x => x.TenantId).HasMaxLength(64).IsRequired();
            e.HasOne(x => x.BaseModel).WithMany().HasForeignKey(x => x.BaseModelId);
        });

        modelBuilder.Entity<ModerationRule>(e =>
        {
            e.HasKey(x => x.Id);
            e.Property(x => x.Pattern).IsRequired();
            e.Property(x => x.Action).HasMaxLength(32).IsRequired();
            e.Property(x => x.Category).HasMaxLength(64).IsRequired();
        });

        modelBuilder.Entity<BackupHistory>(e =>
        {
            e.HasKey(x => x.Id);
            e.HasIndex(x => x.Timestamp);
            e.Property(x => x.Path).HasMaxLength(1024).IsRequired();
            e.Property(x => x.Kind).HasMaxLength(32).IsRequired();
        });
    }
}
