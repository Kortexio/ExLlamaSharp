using System.Security.Cryptography;
using System.Text;
using ExLlamaSharp.Server.Data;
using ExLlamaSharp.Server.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;

namespace ExLlamaSharp.Server.Services;

public sealed class AbTestRouter
{
    private readonly IServiceScopeFactory _scopeFactory;

    public AbTestRouter(IServiceScopeFactory scopeFactory)
    {
        _scopeFactory = scopeFactory;
    }

    public async Task<AbTestRouteResult?> RouteAsync(Guid abTestId, string requestId, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        await using var scope = _scopeFactory.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
        var test = await db.AbTests.AsNoTracking()
            .FirstOrDefaultAsync(t => t.Id == abTestId && t.Active, cancellationToken)
            .ConfigureAwait(false);

        if (test is null)
        {
            return null;
        }

        return Route(test, requestId);
    }

    public AbTestRouteResult Route(AbTest test, string requestId)
    {
        ArgumentNullException.ThrowIfNull(test);
        ArgumentException.ThrowIfNullOrWhiteSpace(requestId);

        var hash = GetConsistentHash($"{test.Id:N}:{requestId}");
        var bucket = hash % 100;
        var threshold = (int)Math.Clamp(test.SplitRatio * 100.0, 0, 100);
        var useA = bucket < threshold;

        return new AbTestRouteResult
        {
            AbTestId = test.Id,
            ModelId = useA ? test.ModelAId : test.ModelBId,
            Variant = useA ? "A" : "B",
        };
    }

    /// <summary>Stable 0..99 hash for consistent assignment.</summary>
    public static int GetConsistentHash(string input)
    {
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(input));
        var value = BitConverter.ToUInt32(bytes, 0);
        return (int)(value % 100);
    }
}

public sealed class AbTestRouteResult
{
    public Guid AbTestId { get; init; }
    public Guid ModelId { get; init; }
    public string Variant { get; init; } = "A";
}
