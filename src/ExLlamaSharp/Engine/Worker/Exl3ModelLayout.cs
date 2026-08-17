using System.Text.Json;

namespace ExLlamaSharp.Engine.Worker;

/// <summary>Detects on-disk EXL3 model folders (config + weights + tokenizer).</summary>
internal static class Exl3ModelLayout
{
    public static bool LooksLikeExl3Directory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("mock://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Directory.Exists(path))
        {
            return false;
        }

        var configPath = Path.Combine(path, "config.json");
        var hasSafetensors = Directory.EnumerateFiles(path, "*.safetensors").Any();
        var hasTokenizer = File.Exists(Path.Combine(path, "tokenizer.json"));

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                if (json.Contains("exl3", StringComparison.OrdinalIgnoreCase) ||
                    json.Contains("\"quant_method\"", StringComparison.OrdinalIgnoreCase) &&
                    json.Contains("exl3", StringComparison.OrdinalIgnoreCase))
                {
                    return hasSafetensors || hasTokenizer;
                }

                using var doc = JsonDocument.Parse(json);
                if (ContainsExl3(doc.RootElement))
                {
                    return true;
                }
            }
            catch
            {
                // fall through
            }
        }

        return hasSafetensors && hasTokenizer && File.Exists(configPath);
    }

    private static bool ContainsExl3(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (p.Name.Contains("quant", StringComparison.OrdinalIgnoreCase) &&
                        p.Value.ValueKind == JsonValueKind.String &&
                        p.Value.GetString()?.Contains("exl3", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return true;
                    }

                    if (ContainsExl3(p.Value))
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var i in el.EnumerateArray())
                {
                    if (ContainsExl3(i))
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.String:
                return el.GetString()?.Contains("exl3", StringComparison.OrdinalIgnoreCase) == true;
        }

        return false;
    }
}
