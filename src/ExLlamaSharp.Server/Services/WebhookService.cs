using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace ExLlamaSharp.Server.Services;

public sealed class WebhookService
{
    private readonly IHttpClientFactory _httpClientFactory;
    private readonly SettingsService _settings;
    private readonly ILogger<WebhookService> _logger;

    public WebhookService(
        IHttpClientFactory httpClientFactory,
        SettingsService settings,
        ILogger<WebhookService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _settings = settings;
        _logger = logger;
    }

    public async Task<bool> SendAsync(string eventName, object payload, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(eventName);
        ArgumentNullException.ThrowIfNull(payload);

        var settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(settings.WebhookUrl))
        {
            return false;
        }

        var body = JsonSerializer.Serialize(new
        {
            @event = eventName,
            timestamp = DateTime.UtcNow,
            data = payload,
        });

        var secret = settings.WebhookSecret ?? string.Empty;
        var signature = ComputeHmacSha256(secret, body);

        for (var attempt = 1; attempt <= 3; attempt++)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, settings.WebhookUrl)
                {
                    Content = new StringContent(body, Encoding.UTF8, "application/json"),
                };
                request.Headers.Add("X-ExLlamaSharp-Event", eventName);
                request.Headers.Add("X-ExLlamaSharp-Signature", $"sha256={signature}");
                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                var client = _httpClientFactory.CreateClient(nameof(WebhookService));
                using var response = await client.SendAsync(request, cancellationToken).ConfigureAwait(false);
                if (response.IsSuccessStatusCode)
                {
                    return true;
                }

                _logger.LogWarning(
                    "Webhook attempt {Attempt}/3 returned {Status}",
                    attempt,
                    (int)response.StatusCode);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Webhook attempt {Attempt}/3 failed", attempt);
            }

            if (attempt < 3)
            {
                await Task.Delay(TimeSpan.FromSeconds(attempt), cancellationToken).ConfigureAwait(false);
            }
        }

        return false;
    }

    internal static string ComputeHmacSha256(string secret, string body)
    {
        var key = Encoding.UTF8.GetBytes(secret);
        var data = Encoding.UTF8.GetBytes(body);
        var hash = HMACSHA256.HashData(key, data);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }
}
