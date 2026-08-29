using System.Net.Http.Headers;
using Microsoft.Extensions.Configuration;

namespace ExLlamaSharp.Server.Services.Ui;

/// <summary>
/// Attaches Bearer auth for the "local-api" client from the browser cookie
/// or configured AdminApiKey (fallback for first-run / seed key).
/// </summary>
public sealed class LocalApiAuthHandler : DelegatingHandler
{
    private readonly IHttpContextAccessor _http;
    private readonly IConfiguration _config;

    public LocalApiAuthHandler(IHttpContextAccessor http, IConfiguration config)
    {
        _http = http;
        _config = config;
    }

    protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        if (request.Headers.Authorization is null)
        {
            string? key = null;
            if (_http.HttpContext?.Request.Cookies.TryGetValue("exllamasharp_key", out var cookie) == true
                && !string.IsNullOrWhiteSpace(cookie))
            {
                key = cookie;
            }

            key ??= _config["ExLlamaSharp:AdminApiKey"];
            if (!string.IsNullOrWhiteSpace(key))
            {
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", key);
            }
        }

        return base.SendAsync(request, cancellationToken);
    }
}
