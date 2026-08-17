namespace ExLlamaSharp.Server.Services;

/// <summary>Supported / planned weight quantization modes.</summary>
public enum QuantizationMode
{
    EXL3 = 0,
    EXL2 = 1,
    INT8 = 2,
    FP8 = 3,
    AWQ = 4,
    GPTQ = 5,
    Dynamic = 6,
}

/// <summary>Parse / display helpers for <see cref="QuantizationMode"/>.</summary>
public static class QuantizationModes
{
    public static bool TryParse(string? value, out QuantizationMode mode)
    {
        mode = default;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var normalized = value.Trim().ToUpperInvariant().Replace('-', '_').Replace(' ', '_');
        return Enum.TryParse(normalized, ignoreCase: true, out mode);
    }

    public static QuantizationMode Parse(string value)
    {
        if (!TryParse(value, out var mode))
        {
            throw new ArgumentException($"Unknown quantization mode: {value}", nameof(value));
        }

        return mode;
    }

    public static string ToWireName(QuantizationMode mode) => mode switch
    {
        QuantizationMode.EXL3 => "exl3",
        QuantizationMode.EXL2 => "exl2",
        QuantizationMode.INT8 => "int8",
        QuantizationMode.FP8 => "fp8",
        QuantizationMode.AWQ => "awq",
        QuantizationMode.GPTQ => "gptq",
        QuantizationMode.Dynamic => "dynamic",
        _ => mode.ToString().ToLowerInvariant(),
    };

    public static IReadOnlyList<QuantizationMode> All { get; } =
    [
        QuantizationMode.EXL3,
        QuantizationMode.EXL2,
        QuantizationMode.INT8,
        QuantizationMode.FP8,
        QuantizationMode.AWQ,
        QuantizationMode.GPTQ,
        QuantizationMode.Dynamic,
    ];

    public static bool IsExLlamaFamily(QuantizationMode mode) =>
        mode is QuantizationMode.EXL3 or QuantizationMode.EXL2 or QuantizationMode.Dynamic;
}
