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

            if (SuggestsVisionFromConfigElement(root))
            {
                return ModelArchitecture.Llava;
            }

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

        if (SuggestsVisionFromText(lower))
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

    /// <summary>
    /// True when a model directory looks multimodal (LLaVA / vision tower / mm_projector).
    /// </summary>
    public bool SuggestsVisionFromDirectory(string? modelPath)
    {
        if (string.IsNullOrWhiteSpace(modelPath) || !Directory.Exists(modelPath))
        {
            return false;
        }

        var configPath = Path.Combine(modelPath, "config.json");
        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                if (SuggestsVisionFromConfigJson(json))
                {
                    return true;
                }
            }
            catch
            {
                // ignore IO/parse errors
            }
        }

        // Path / folder name heuristics (e.g. .../llava-v1.5-7b-exl3)
        var name = Path.GetFileName(modelPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (SuggestsVisionFromText(name))
        {
            return true;
        }

        // Common multimodal weight files next to config
        foreach (var marker in new[] { "mm_projector.bin", "mm_projector.safetensors", "vision_tower", "clip_vision" })
        {
            if (File.Exists(Path.Combine(modelPath, marker))
                || Directory.Exists(Path.Combine(modelPath, marker)))
            {
                return true;
            }
        }

        return false;
    }

    public bool SuggestsVisionFromConfigJson(string configJson)
    {
        if (string.IsNullOrWhiteSpace(configJson))
        {
            return false;
        }

        try
        {
            using var doc = JsonDocument.Parse(configJson);
            if (SuggestsVisionFromConfigElement(doc.RootElement))
            {
                return true;
            }
        }
        catch (JsonException)
        {
            // fall through
        }

        return SuggestsVisionFromText(configJson);
    }

    public static string ToDisplayName(ModelArchitecture architecture) => architecture switch
    {
        ModelArchitecture.Llama => "Llama",
        ModelArchitecture.Qwen => "Qwen",
        ModelArchitecture.Mixtral => "Mixtral",
        ModelArchitecture.Llava => "LLaVA",
        _ => "Unknown",
    };

    private static bool SuggestsVisionFromConfigElement(JsonElement root)
    {
        if (root.TryGetProperty("model_type", out var mt) && SuggestsVisionFromText(mt.GetString()))
        {
            return true;
        }

        if (root.TryGetProperty("architectures", out var archArr) && archArr.ValueKind == JsonValueKind.Array)
        {
            foreach (var el in archArr.EnumerateArray())
            {
                if (SuggestsVisionFromText(el.GetString()))
                {
                    return true;
                }
            }
        }

        foreach (var key in new[] { "vision_config", "mm_projector_type", "mm_vision_tower", "image_token_index", "vision_tower" })
        {
            if (root.TryGetProperty(key, out _))
            {
                return true;
            }
        }

        if (root.TryGetProperty("auto_map", out var autoMap) && autoMap.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in autoMap.EnumerateObject())
            {
                if (SuggestsVisionFromText(prop.Value.GetString()))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool SuggestsVisionFromText(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var lower = text.ToLowerInvariant();
        if (LlavaRegex().IsMatch(lower) || lower.Contains("llava", StringComparison.Ordinal))
        {
            return true;
        }

        if (lower.Contains("mm_projector", StringComparison.Ordinal)
            || lower.Contains("vision_tower", StringComparison.Ordinal)
            || lower.Contains("multimodal", StringComparison.Ordinal)
            || lower.Contains("vision_config", StringComparison.Ordinal)
            || lower.Contains("clip_vision", StringComparison.Ordinal)
            || lower.Contains("idefics", StringComparison.Ordinal)
            || lower.Contains("qwen2-vl", StringComparison.Ordinal)
            || lower.Contains("qwen2_vl", StringComparison.Ordinal)
            || lower.Contains("internvl", StringComparison.Ordinal)
            || VisionRegex().IsMatch(lower))
        {
            return true;
        }

        return false;
    }

    private static ModelArchitecture MapToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return ModelArchitecture.Unknown;
        }

        var t = token.Trim().ToLowerInvariant();
        if (SuggestsVisionFromText(t))
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

    [GeneratedRegex(@"\b(vision|multimodal)\b", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex VisionRegex();

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
