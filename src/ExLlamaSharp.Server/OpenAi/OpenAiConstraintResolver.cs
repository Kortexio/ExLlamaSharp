using ExLlamaSharp.Engine;

namespace ExLlamaSharp.Server.OpenAi;

public sealed class OpenAiConstraintPlan
{
    public string? ToolsMode { get; init; }
    public string? StructuredOutputMode { get; init; }
    public bool SuppressPromptJsonSchema { get; init; }
    public string? ConstraintType { get; init; }
    public string? ConstraintSchemaJson { get; init; }
    public string? ConstraintBackend { get; init; }
}

public static class OpenAiConstraintResolver
{
    public static async Task<OpenAiConstraintPlan> ResolveAsync(
        IInferenceEngine engine,
        bool parseTools,
        ToolCallValidator.ToolChoiceRequirement toolChoice,
        HashSet<string> allowedToolNames,
        string? jsonSchema,
        bool hasResponseFormat,
        CancellationToken cancellationToken)
    {
        var llguidance = false;
        if (engine is ExLlamaV3WorkerEngine worker)
        {
            await worker.EnsureRuntimeInfoAsync(cancellationToken).ConfigureAwait(false);
            llguidance = worker.RuntimeCapabilities?.Llguidance == true;
        }

        string? toolsMode = null;
        string? structuredMode = null;
        var suppressPrompt = false;
        string? constraintType = null;
        string? constraintSchema = null;
        string? constraintBackend = null;

        if (parseTools)
        {
            if (llguidance)
            {
                toolsMode = ToolCallValidator.ToolsModeConstrained;
                constraintType = "json_schema";
                constraintSchema = OpenAiToolOutputSchema.BuildEnvelopeSchema(
                    allowedToolNames.ToList(),
                    toolChoice.RequiredFunctionName);
                constraintBackend = "llguidance";
                suppressPrompt = true;
            }
            else
            {
                toolsMode = ToolCallValidator.ToolsModePromptParse;
            }
        }

        if (hasResponseFormat && !string.IsNullOrWhiteSpace(jsonSchema))
        {
            if (llguidance && !parseTools)
            {
                structuredMode = ToolCallValidator.StructuredOutputConstrained;
                constraintType = "json_schema";
                constraintSchema = jsonSchema;
                constraintBackend = "llguidance";
                suppressPrompt = true;
            }
            else if (llguidance && parseTools)
            {
                structuredMode = ToolCallValidator.StructuredOutputConstrained;
            }
            else
            {
                structuredMode = ToolCallValidator.StructuredOutputPromptOnly;
            }
        }

        return new OpenAiConstraintPlan
        {
            ToolsMode = toolsMode,
            StructuredOutputMode = structuredMode,
            SuppressPromptJsonSchema = suppressPrompt,
            ConstraintType = constraintType,
            ConstraintSchemaJson = constraintSchema,
            ConstraintBackend = constraintBackend,
        };
    }
}