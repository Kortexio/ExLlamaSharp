using System.Security.Cryptography;
using System.Text;

namespace ExLlamaSharp.Server.Services;

/// <summary>
/// Deterministic embedding stub (dim 384). Replace with ONNX sentence-transformers later.
/// </summary>
public sealed class EmbeddingService
{
    public const int Dimensions = 384;

    public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(Embed(text));
    }

    public Task<IReadOnlyList<float[]>> EmbedBatchAsync(IEnumerable<string> texts, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(texts);
        var list = new List<float[]>();
        foreach (var text in texts)
        {
            cancellationToken.ThrowIfCancellationRequested();
            list.Add(Embed(text));
        }

        return Task.FromResult<IReadOnlyList<float[]>>(list);
    }

    public float[] Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var vector = new float[Dimensions];
        var seed = BitConverter.ToUInt64(hash, 0);

        // Expand deterministic pseudo-random floats from hash seed.
        for (var i = 0; i < Dimensions; i++)
        {
            seed = seed * 6364136223846793005UL + 1UL;
            var bits = (uint)(seed >> 33);
            vector[i] = (bits / (float)uint.MaxValue) * 2f - 1f;
        }

        // Mix in character stats for more text sensitivity.
        var sum = 0.0;
        for (var i = 0; i < text.Length; i++)
        {
            vector[i % Dimensions] += (text[i] % 97) / 97f * 0.01f;
        }

        for (var i = 0; i < Dimensions; i++)
        {
            sum += vector[i] * vector[i];
        }

        var norm = Math.Sqrt(sum);
        if (norm > 1e-12)
        {
            for (var i = 0; i < Dimensions; i++)
            {
                vector[i] = (float)(vector[i] / norm);
            }
        }

        return vector;
    }
}
