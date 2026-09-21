using ExLlamaSharp.Server.OpenAi;

namespace ExLlamaSharp.Server.Tests;

public sealed class ToolCallValidatorTests
{
    [Fact]
    public void Required_tool_choice_fails_when_no_tool_calls_parsed()
    {
        var req = new ToolCallValidator.ToolChoiceRequirement(true, null);
        var err = ToolCallValidator.ValidateToolResponse("Hello", [], req);
        Assert.NotNull(err);
    }

    [Fact]
    public void Validates_function_name_against_allowlist()
    {
        var json = """{"tool_calls":[{"id":"c1","type":"function","function":{"name":"search","arguments":"{}"}}]}""";
        var req = new ToolCallValidator.ToolChoiceRequirement(false, null);
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "search" };
        Assert.Null(ToolCallValidator.ValidateToolResponse(json, allowed, req));
    }

    [Fact]
    public void Rejects_unknown_function_name()
    {
        var json = """{"tool_calls":[{"id":"c1","type":"function","function":{"name":"evil","arguments":"{}"}}]}""";
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "search" };
        var err = ToolCallValidator.ValidateToolResponse(json, allowed, new ToolCallValidator.ToolChoiceRequirement(false, null));
        Assert.Contains("unknown", err, StringComparison.OrdinalIgnoreCase);
    }
}