namespace ExLlamaSharp.Server.Services.Ui;

/// <summary>Per-circuit admin API key for Blazor Server → local HTTP calls.</summary>
public sealed class AdminUiSession
{
    public string? ApiKey { get; set; }
}
