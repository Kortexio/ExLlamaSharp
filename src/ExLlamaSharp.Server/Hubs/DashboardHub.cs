using Microsoft.AspNetCore.SignalR;

namespace ExLlamaSharp.Server.Hubs;

/// <summary>
/// SignalR hub for live dashboard metrics.
/// Callers that broadcast should throttle (e.g. max once per 1–2 seconds) so clients
/// are not flooded under high inference load.
/// </summary>
public sealed class DashboardHub : Hub
{
    public const string MetricsMethod = "metrics";

    public Task SubscribeAsync(string channel = "default")
    {
        return Groups.AddToGroupAsync(Context.ConnectionId, channel);
    }

    public Task UnsubscribeAsync(string channel = "default")
    {
        return Groups.RemoveFromGroupAsync(Context.ConnectionId, channel);
    }

    /// <summary>
    /// Optional client ping for connectivity checks. Prefer server-push via
    /// IHubContext&lt;DashboardHub&gt; with throttling rather than high-frequency client polls.
    /// </summary>
    public Task<object> PingAsync() =>
        Task.FromResult<object>(new { ok = true, utc = DateTime.UtcNow });
}
