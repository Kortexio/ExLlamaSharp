using ExLlamaSharp.Server.OpenAi;

namespace ExLlamaSharp.Server.Tests;

public sealed class ToolCallParserTests
{
    [Fact]
    public void Parses_plain_tool_calls_object()
    {
        var json = """{"tool_calls":[{"id":"call_1","type":"function","function":{"name":"get_weather","arguments":"{\"city\":\"Lisbon\"}"}}]}""";
        Assert.True(ToolCallParser.TryParse(json, out var calls, out var residual));
        Assert.Single(calls);
        Assert.Equal("get_weather", calls[0].Function.Name);
        Assert.Null(residual);
    }

    [Fact]
    public void Parses_fenced_json()
    {
        var text = """
            ```json
            {"tool_calls":[{"id":"c1","type":"function","function":{"name":"search","arguments":"{}"}}]}
            ```
            """;
        Assert.True(ToolCallParser.TryParse(text, out var calls, out _));
        Assert.Equal("search", calls[0].Function.Name);
    }

    [Fact]
    public void Returns_false_for_plain_text()
    {
        Assert.False(ToolCallParser.TryParse("Hello there", out var calls, out var residual));
        Assert.Empty(calls);
        Assert.Equal("Hello there", residual);
    }
}
