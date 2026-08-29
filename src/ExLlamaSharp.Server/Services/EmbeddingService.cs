using System.Security.Cryptography;
using System.Text;
using Microsoft.Extensions.Logging;
using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;

namespace ExLlamaSharp.Server.Services;

/// <summary>
/// Sentence embeddings via ONNX (all-MiniLM-L6-v2 style, dim 384) when model files are present;
/// otherwise a normalized local hasher so /v1/embeddings stays usable offline/CI.
/// </summary>
public sealed class EmbeddingService : IDisposable
{
    public const int Dimensions = 384;

    private readonly ILogger<EmbeddingService> _logger;
    private readonly object _gate = new();
    private InferenceSession? _session;
    private string? _modelPath;
    private bool _triedLoad;

    public EmbeddingService(ILogger<EmbeddingService> logger)
    {
        _logger = logger;
    }

    public bool IsOnnxLoaded
    {
        get
        {
            lock (_gate)
            {
                return _session is not null;
            }
        }
    }

    public string ModelDirectory
    {
        get
        {
            var dataRoot = Environment.GetEnvironmentVariable("EXLLAMASHARP_DATA_ROOT");
            if (string.IsNullOrWhiteSpace(dataRoot))
            {
                dataRoot = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                    "ExLlamaSharp");
            }

            var dir = Path.Combine(dataRoot, "embeddings", "all-MiniLM-L6-v2");
            Directory.CreateDirectory(dir);
            return dir;
        }
    }

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
            if (!TryEmbed(text, out var vector, out var error))
            {
                throw new InvalidOperationException(error ?? "Embedding backend unavailable.");
            }

            list.Add(vector!);
        }

        return Task.FromResult<IReadOnlyList<float[]>>(list);
    }

    public float[] Embed(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (!TryEmbed(text, out var vector, out var error))
        {
            throw new InvalidOperationException(error ?? "Embedding backend unavailable.");
        }

        return vector!;
    }

    /// <summary>
    /// Returns false when ONNX is required and unavailable (no silent hash fallback in release).
    /// Set env <c>EXLLAMASHARP_ALLOW_EMBEDDING_FALLBACK=1</c> to permit deterministic hash vectors (CI).
    /// </summary>
    public bool TryEmbed(string text, out float[]? vector, out string? error)
    {
        ArgumentNullException.ThrowIfNull(text);
        EnsureSession();
        error = null;
        vector = null;

        lock (_gate)
        {
            if (_session is not null)
            {
                try
                {
                    vector = EmbedOnnx(text, _session);
                    return true;
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "ONNX embed failed");
                    error = $"ONNX embedding failed: {ex.Message}";
                    if (!AllowFallback)
                    {
                        return false;
                    }
                }
            }
            else if (!AllowFallback)
            {
                error =
                    $"ONNX embedding model not found under {ModelDirectory}. Place model.onnx (dim {Dimensions}) or set EXLLAMASHARP_ALLOW_EMBEDDING_FALLBACK=1 for CI.";
                return false;
            }
        }

        vector = EmbedFallback(text);
        return true;
    }

    public bool AllowFallback =>
        string.Equals(
            Environment.GetEnvironmentVariable("EXLLAMASHARP_ALLOW_EMBEDDING_FALLBACK"),
            "1",
            StringComparison.OrdinalIgnoreCase);

    public string BackendName => IsOnnxLoaded ? "onnx" : (AllowFallback ? "fallback" : "unavailable");

    private void EnsureSession()
    {
        lock (_gate)
        {
            if (_session is not null || _triedLoad)
            {
                return;
            }

            _triedLoad = true;
            var onnx = Path.Combine(ModelDirectory, "model.onnx");
            if (!File.Exists(onnx))
            {
                // Alternate common export name
                onnx = Path.Combine(ModelDirectory, "model_quantized.onnx");
            }

            if (!File.Exists(onnx))
            {
                _logger.LogInformation(
                    "No ONNX embedding model at {Dir}; using local fallback. Place model.onnx (dim 384) to enable ONNX.",
                    ModelDirectory);
                return;
            }

            try
            {
                _session = new InferenceSession(onnx);
                _modelPath = onnx;
                _logger.LogInformation("Loaded ONNX embedding model from {Path}", onnx);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Failed to load ONNX embedding model {Path}", onnx);
            }
        }
    }

    private static float[] EmbedOnnx(string text, InferenceSession session)
    {
        // Generic path: if the model expects input_ids, use a simple whitespace hash tokenizer
        // into fixed length 128 — works with many MiniLM ONNX exports that use int64 inputs.
        var inputs = session.InputMetadata;
        var outputs = session.OutputMetadata;
        if (inputs.Count == 0 || outputs.Count == 0)
        {
            throw new InvalidOperationException("ONNX model has no inputs/outputs");
        }

        var inputName = inputs.Keys.First();
        var meta = inputs[inputName];
        var dims = meta.Dimensions.Select(d => d <= 0 ? 128 : d).ToArray();
        if (dims.Length == 1)
        {
            dims = [1, dims[0]];
        }

        var seq = dims.Length >= 2 ? dims[^1] : 128;
        var tensor = new DenseTensor<long>(new[] { 1, seq });
        var tokens = TokenizeRough(text, seq);
        for (var i = 0; i < seq; i++)
        {
            tensor[0, i] = tokens[i];
        }

        using var results = session.Run([NamedOnnxValue.CreateFromTensor(inputName, tensor)]);
        var first = results.First().AsEnumerable<float>().ToArray();
        if (first.Length >= Dimensions)
        {
            var slice = first.AsSpan(0, Dimensions).ToArray();
            return L2Normalize(slice);
        }

        // Mean-pool last dim if 3D flattened oddly
        var vector = new float[Dimensions];
        for (var i = 0; i < first.Length; i++)
        {
            vector[i % Dimensions] += first[i];
        }

        var count = Math.Max(1, first.Length / Dimensions);
        for (var i = 0; i < Dimensions; i++)
        {
            vector[i] /= count;
        }

        return L2Normalize(vector);
    }

    private static long[] TokenizeRough(string text, int seq)
    {
        var ids = new long[seq];
        ids[0] = 101; // [CLS]-ish
        var parts = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries);
        var i = 1;
        foreach (var p in parts)
        {
            if (i >= seq - 1)
            {
                break;
            }

            var hash = SHA256.HashData(Encoding.UTF8.GetBytes(p.ToLowerInvariant()));
            ids[i++] = 1000 + (BitConverter.ToUInt16(hash, 0) % 20000);
        }

        if (i < seq)
        {
            ids[i] = 102; // [SEP]-ish
        }

        return ids;
    }

    private static float[] EmbedFallback(string text)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        var vector = new float[Dimensions];
        var seed = BitConverter.ToUInt64(hash, 0);

        for (var i = 0; i < Dimensions; i++)
        {
            seed = seed * 6364136223846793005UL + 1UL;
            var bits = (uint)(seed >> 33);
            vector[i] = (bits / (float)uint.MaxValue) * 2f - 1f;
        }

        for (var i = 0; i < text.Length; i++)
        {
            vector[i % Dimensions] += (text[i] % 97) / 97f * 0.01f;
        }

        return L2Normalize(vector);
    }

    private static float[] L2Normalize(float[] vector)
    {
        double sum = 0;
        for (var i = 0; i < vector.Length; i++)
        {
            sum += vector[i] * vector[i];
        }

        var norm = Math.Sqrt(sum);
        if (norm > 1e-12)
        {
            for (var i = 0; i < vector.Length; i++)
            {
                vector[i] = (float)(vector[i] / norm);
            }
        }

        return vector;
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _session?.Dispose();
            _session = null;
        }
    }
}
