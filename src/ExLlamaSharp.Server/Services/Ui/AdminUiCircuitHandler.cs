using Microsoft.AspNetCore.Components.Server.Circuits;

namespace ExLlamaSharp.Server.Services.Ui;

/// <summary>
/// Copies the admin cookie into the circuit-scoped session. Blazor Server
/// <see cref="IHttpContextAccessor"/> is often null after the SignalR circuit opens,
/// which would otherwise leave local-api Chat/Models calls unauthenticated.
/// </summary>
public sealed class AdminUiCircuitHandler : CircuitHandler
{
    private readonly IHttpContextAccessor _http;
    private readonly AdminUiSession _session;

    public AdminUiCircuitHandler(IHttpContextAccessor http, AdminUiSession session)
    {
        _http = http;
        _session = session;
    }

    public override Task OnCircuitOpenedAsync(Circuit circuit, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_session.ApiKey)
            && _http.HttpContext?.Request.Cookies.TryGetValue("exllamasharp_key", out var key) == true
            && !string.IsNullOrWhiteSpace(key))
        {
            _session.ApiKey = key;
        }

        return Task.CompletedTask;
    }
}
