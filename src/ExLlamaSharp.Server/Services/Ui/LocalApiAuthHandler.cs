using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ExLlamaSharp.Server.Services.Ui;

/// <summary>
/// Attaches Bearer auth for the "local-api" client from the circuit session,
/// browser cookie, or configured AdminApiKey (Development fallback).
/// </summary>
public sealed class LocalApiAuthHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _http;
    private readonly IServiceProvider _services;
    private readonly IConfiguration _config;
    private readonly IHostEnvironment _environment;

    public LocalApiAuthHandler(
        IHttpContextAccessor http,
        IServiceProvider services,
        IConfiguration config,
        IHostEnvironment environment)
    {
        _http = http;
        _services = services;
        _config = config;
        _environment = environment;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null)
        {
            string? key = null;
            try
            {
                key = _services.GetService<AdminUiSession>()?.ApiKey;
            }
            catch (InvalidOperationException)
            {
                // Handler resolved outside a circuit scope.
            }

            if (string.IsNullOrWhiteSpace(key)
                && _http.HttpContext?.Request.Cookies.TryGetValue("exllamasharp_key", out var cookie) == true
                && !string.IsNullOrWhiteSpace(cookie))
            {
                key = cookie;
            }

            if (_environment.IsDevelopment())
            {
                key ??= _config["ExLlamaSharp:AdminApiKey"];
            }

            if (!string.IsNullOrWhiteSpace(key))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
