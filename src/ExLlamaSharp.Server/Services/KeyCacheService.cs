using ExLlamaSharp.Server.Data;
using ExLlamaSharp.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

namespace ExLlamaSharp.Server.Services;

public sealed class KeyCacheService
{
    private readonly IMemoryCache _cache;
    private readonly IServiceScopeFactory _scopeFactory;

    public KeyCacheService(IMemoryCache cache, IServiceScopeFactory scopeFactory)
    {
        _cache = cache;
        _scopeFactory = scopeFactory;
    }

    public async Task<ApiKey?> GetAsync(string keyHash, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(keyHash);

        if (_cache.TryGetValue(CacheKey(keyHash), out ApiKey? cached))
        {
            return cached;
        }

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

        var key = await db.ApiKeys
            .AsNoTracking()
            .FirstOrDefaultAsync(
                k => k.KeyHash == keyHash && !k.Revoked && (k.ExpiresAt == null || k.ExpiresAt > DateTime.UtcNow),
                cancellationToken)
            .ConfigureAwait(false);

        if (key is not null)
        {
            var options = new MemoryCacheEntryOptions
            {
                AbsoluteExpirationRelativeToNow = TimeSpan.FromSeconds(60),
            };
            _cache.Set(CacheKey(keyHash), key, options);
            _cache.Set(IdIndexKey(key.Id), keyHash, options);
        }

        return key;
    }

    public void Invalidate(string keyHash)
    {
        if (!string.IsNullOrWhiteSpace(keyHash))
        {
            _cache.Remove(CacheKey(keyHash));
        }
    }

    public void Invalidate(Guid keyId)
    {
        if (_cache.TryGetValue(IdIndexKey(keyId), out string? hash) && !string.IsNullOrWhiteSpace(hash))
        {
            _cache.Remove(CacheKey(hash));
            _cache.Remove(IdIndexKey(keyId));
        }
    }

    private static string CacheKey(string keyHash) => $"apikey:{keyHash}";

    private static string IdIndexKey(Guid keyId) => $"apikey:id:{keyId:N}";
}
