using System.Text.Json;

namespace ExLlamaSharp.Server.OpenAi;

public static class OpenAiToolOutputSchema
{
    public static string BuildEnvelopeSchema(IReadOnlyList<string> functionNames, string? requiredFunctionName)
    {
        var names = functionNames.Where(n => !string.IsNullOrWhiteSpace(n)).Distinct(StringComparer.Ordinal).ToList();
        object nameSchema;
        if (!string.IsNullOrWhiteSpace(requiredFunctionName))
        {
            nameSchema = new { type = "string", @const = requiredFunctionName };
        }
        else if (names.Count > 0)
        {
            nameSchema = new { type = "string", @enum = names };
        }
        else
        {
            nameSchema = new { type = "string" };
        }

        var schema = new
        {
            type = "object",
            additionalProperties = false,
            required = new[] { "tool_calls" },
            properties = new
            {
                tool_calls = new
                {
                    type = "array",
                    minItems = 1,
                    items = new
                    {
                        type = "object",
                        additionalProperties = false,
                        required = new[] { "id", "type", "function" },
                        properties = new
                        {
                            id = new { type = "string" },
                            type = new { type = "string", @const = "function" },
                            function = new
                            {
                                type = "object",
                                additionalProperties = false,
                                required = new[] { "name", "arguments" },
                                properties = new
                                {
                                    name = nameSchema,
                                    arguments = new { type = "string" },
                                },
                            },
                        },
                    },
                },
            },
        };

        return JsonSerializer.Serialize(schema);
    }
}