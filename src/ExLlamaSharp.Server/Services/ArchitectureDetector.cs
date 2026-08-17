using System.Text.Json;
using System.Text.RegularExpressions;

namespace ExLlamaSharp.Server.Services;

/// <summary>
/// Detects common HF architecture families from <c>config.json</c> text.
/// </summary>
public sealed partial class ArchitectureDetector
{
    public ModelArchitecture DetectFromConfigJson(string configJson)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(configJson);

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            var root = doc.RootElement;

            if (root.TryGetProperty("model_type", out var modelTypeEl))
            {
                var hit = MapToken(modelTypeEl.GetString());
                if (hit != ModelArchitecture.Unknown)
                {
                    return hit;
                }
            }

            if (root.TryGetProperty("architectures", out var archArr) && archArr.ValueKind == JsonValueKind.Array)
            {
                foreach (var el in archArr.EnumerateArray())
                {
                    var hit = MapToken(el.GetString());
                    if (hit != ModelArchitecture.Unknown)
                    {
                        return hit;
                    }
                }
            }
        }
        catch (JsonException)
        {
            // fall through to regex heuristics
        }

        return DetectFromLooseText(configJson);
    }

    public ModelArchitecture DetectFromLooseText(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return ModelArchitecture.Unknown;
        }

        var lower = text.ToLowerInvariant();

        if (LlavaRegex().IsMatch(lower) || lower.Contains("llava", StringComparison.Ordinal))
        {
            return ModelArchitecture.Llava;
        }

        if (MixtralRegex().IsMatch(lower) || lower.Contains("mixtral", StringComparison.Ordinal))
        {
            return ModelArchitecture.Mixtral;
        }

        if (QwenRegex().IsMatch(lower) || lower.Contains("qwen", StringComparison.Ordinal))
        {
            return ModelArchitecture.Qwen;
        }

        if (LlamaRegex().IsMatch(lower) || lower.Contains("llama", StringComparison.Ordinal))
        {
            return ModelArchitecture.Llama;
        }

        return ModelArchitecture.Unknown;
    }

    public static string ToDisplayName(ModelArchitecture architecture) => architecture switch
    {
        ModelArchitecture.Llama => "Llama",
        ModelArchitecture.Qwen => "Qwen",
        ModelArchitecture.Mixtral => "Mixtral",
        ModelArchitecture.Llava => "LLaVA",
        _ => "Unknown",
    };

    private static ModelArchitecture MapToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return ModelArchitecture.Unknown;
        }

        var t = token.Trim().ToLowerInvariant();
        if (t.Contains("llava", StringComparison.Ordinal))
        {
            return ModelArchitecture.Llava;
        }

        if (t.Contains("mixtral", StringComparison.Ordinal) || t.Contains("mistral", StringComparison.Ordinal) && t.Contains("moe", StringComparison.Ordinal))
        {
            return ModelArchitecture.Mixtral;
        }

        if (t.Contains("qwen", StringComparison.Ordinal))
        {
            return ModelArchitecture.Qwen;
        }

        if (t.Contains("llama", StringComparison.Ordinal))
        {
            return ModelArchitecture.Llama;
        }

        return ModelArchitecture.Unknown;
    }

    [GeneratedRegex(@"\bllava\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LlavaRegex();

    [GeneratedRegex(@"\bmixtral\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex MixtralRegex();

    [GeneratedRegex(@"\bqwen\d*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex QwenRegex();

    [GeneratedRegex(@"\bllama\d*\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex LlamaRegex();
}

public enum ModelArchitecture
{
    Unknown = 0,
    Llama = 1,
    Qwen = 2,
    Mixtral = 3,
    Llava = 4,
}
