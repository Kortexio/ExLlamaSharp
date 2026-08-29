using ExLlamaSharp.Server.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExLlamaSharp.Server.Tests;

public sealed class EmbeddingServiceTests
{
    [Fact]
    public void Without_onnx_and_without_fallback_TryEmbed_fails()
    {
        Environment.SetEnvironmentVariable("EXLLAMASHARP_ALLOW_EMBEDDING_FALLBACK", null);
        var svc = new EmbeddingService(NullLogger<EmbeddingService>.Instance);
        // Point at empty temp embeddings dir via data root
        var temp = Path.Combine(Path.GetTempPath(), "exl-emb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("EXLLAMASHARP_DATA_ROOT", temp);
        try
        {
            Assert.False(svc.TryEmbed("hello", out var vector, out var error));
            Assert.Null(vector);
            Assert.Contains("ONNX", error ?? "", StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXLLAMASHARP_DATA_ROOT", null);
            try { Directory.Delete(temp, true); } catch { /* ignore */ }
        }
    }

    [Fact]
    public void Fallback_allowed_returns_vector()
    {
        Environment.SetEnvironmentVariable("EXLLAMASHARP_ALLOW_EMBEDDING_FALLBACK", "1");
        var temp = Path.Combine(Path.GetTempPath(), "exl-emb-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        Environment.SetEnvironmentVariable("EXLLAMASHARP_DATA_ROOT", temp);
        try
        {
            var svc = new EmbeddingService(NullLogger<EmbeddingService>.Instance);
            Assert.True(svc.TryEmbed("hello", out var vector, out _));
            Assert.NotNull(vector);
            Assert.Equal(EmbeddingService.Dimensions, vector!.Length);
        }
        finally
        {
            Environment.SetEnvironmentVariable("EXLLAMASHARP_ALLOW_EMBEDDING_FALLBACK", null);
            Environment.SetEnvironmentVariable("EXLLAMASHARP_DATA_ROOT", null);
            try { Directory.Delete(temp, true); } catch { /* ignore */ }
        }
    }
}
