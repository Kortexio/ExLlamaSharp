using ExLlamaSharp.Server.Data.Entities;

namespace ExLlamaSharp.Server.Services;

/// <summary>
/// Speculative decoding draft settings (draft model + look-ahead K).
/// Stub helpers until native draft verification is wired.
/// </summary>
public sealed class SpeculativeDecodingOptions
{
    public const int DefaultDraftK = 5;
    public const int MinDraftK = 1;
    public const int MaxDraftK = 32;

    public bool Enabled { get; init; }
    public Guid? DraftModelId { get; init; }
    public int DraftK { get; init; } = DefaultDraftK;

    public static SpeculativeDecodingOptions FromSettings(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        return new SpeculativeDecodingOptions
        {
            Enabled = settings.SpeculativeEnabled,
            DraftModelId = settings.DraftModelId,
            DraftK = ClampDraftK(settings.DraftK),
        };
    }

    public static SpeculativeDecodingOptions Disabled { get; } = new()
    {
        Enabled = false,
        DraftModelId = null,
        DraftK = DefaultDraftK,
    };

    public static int ClampDraftK(int draftK)
    {
        if (draftK < MinDraftK)
        {
            return MinDraftK;
        }

        if (draftK > MaxDraftK)
        {
            return MaxDraftK;
        }

        return draftK;
    }

    public SpeculativeDecodingOptions WithDraftK(int draftK) => new()
    {
        Enabled = Enabled,
        DraftModelId = DraftModelId,
        DraftK = ClampDraftK(draftK),
    };

    public void ValidateOrThrow()
    {
        if (!Enabled)
        {
            return;
        }

        if (DraftModelId is null || DraftModelId == Guid.Empty)
        {
            throw new InvalidOperationException("Speculative decoding is enabled but DraftModelId is not set.");
        }

        if (DraftK is < MinDraftK or > MaxDraftK)
        {
            throw new InvalidOperationException($"DraftK must be between {MinDraftK} and {MaxDraftK}.");
        }
    }
}
