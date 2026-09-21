using ExLlamaSharp.Server.OpenAi;

namespace ExLlamaSharp.Server.Tests;

public sealed class OpenAiToolOutputSchemaTests
{
    [Fact]
    public void Envelope_includes_tool_names()
    {
        var json = OpenAiToolOutputSchema.BuildEnvelopeSchema(["search", "weather"], null);
        Assert.Contains("search", json, StringComparison.Ordinal);
        Assert.Contains("tool_calls", json, StringComparison.Ordinal);
    }

    [Fact]
    public void Required_function_uses_const()
    {
        var json = OpenAiToolOutputSchema.BuildEnvelopeSchema(["search", "weather"], "search");
        Assert.Contains("\"const\":\"search\"", json.Replace(" ", ""), StringComparison.Ordinal);
    }
}