using ExLlamaSharp.Server.Services;

namespace ExLlamaSharp.Server.Tests;

public sealed class RateLimiterTests
{
    [Fact]
    public void Tpm_does_not_double_count_estimate_and_actual()
    {
        var limiter = new RateLimiter();
        const string key = "key1";
        Assert.True(limiter.TryAcquire(key, rpm: 100, tpm: 100, estimatedTokens: 50, out _));
        // Estimate was not recorded — still room for 100 actual
        limiter.RecordTokens(key, 50);
        Assert.True(limiter.TryAcquire(key, rpm: 100, tpm: 100, estimatedTokens: 50, out _));
        limiter.RecordTokens(key, 50);
        // Now at 100 TPM — next estimate of 1 should fail
        Assert.False(limiter.TryAcquire(key, rpm: 100, tpm: 100, estimatedTokens: 1, out var retry));
        Assert.True(retry >= 1);
    }
}
