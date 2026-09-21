using System.Text.Json;

namespace ExLlamaSharp.Server.Services;

public sealed class EngineRuntimeVersionService
{
    public const string GitHubOwner = "turboderp-org";
    public const string GitHubRepo = "exllamav3";

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<EngineRuntimeVersionService> _logger;

    public EngineRuntimeVersionService(IHttpClientFactory httpClientFactory, ILogger<EngineRuntimeVersionService> logger)
    {
        _httpClientFactory = httpClientFactory;
        _logger = logger;
    }

    public async Task<EngineRuntimeUpdateInfo?> GetLatestReleaseAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            var client = _httpClientFactory.CreateClient("github");
            using var resp = await client
                .GetAsync($"repos/{GitHubOwner}/{GitHubRepo}/releases/latest", cancellationToken)
                .ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return null;
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
            using var doc = await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken).ConfigureAwait(false);
            var root = doc.RootElement;
            var tag = root.TryGetProperty("tag_name", out var tagEl) ? tagEl.GetString() : null;
            var url = root.TryGetProperty("html_url", out var urlEl) ? urlEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(tag))
            {
                return null;
            }

            return new EngineRuntimeUpdateInfo
            {
                LatestTag = tag.TrimStart('v', 'V'),
                ReleaseUrl = url ?? $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest",
            };
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "Failed to fetch exllamav3 latest release");
            return null;
        }
    }

    public static bool IsNewerVersion(string? installed, string? latest)
    {
        if (string.IsNullOrWhiteSpace(installed) || string.IsNullOrWhiteSpace(latest))
        {
            return false;
        }

        if (!Version.TryParse(NormalizeVersion(installed), out var a)
            || !Version.TryParse(NormalizeVersion(latest), out var b))
        {
            return string.Compare(installed, latest, StringComparison.OrdinalIgnoreCase) < 0;
        }

        return b > a;
    }

    private static string NormalizeVersion(string v)
    {
        var plus = v.IndexOf('+', StringComparison.Ordinal);
        if (plus > 0)
        {
            v = v[..plus];
        }

        return v.TrimStart('v', 'V');
    }
}

public sealed class EngineRuntimeUpdateInfo
{
    public string LatestTag { get; init; } = "";
    public string ReleaseUrl { get; init; } = "";
}