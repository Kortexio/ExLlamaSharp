using System.Globalization;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace ExLlamaSharp.Server.Services;

public sealed class UpdateCheckService
{
    public const string GitHubOwner = "Kortexio";
    public const string GitHubRepo = "ExLlamaSharp";
    public const string SetupAssetName = "ExLlamaSharp-Setup-win-x64.exe";

    private static readonly Uri LatestReleaseUri =
        new($"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest");

    private readonly IHttpClientFactory _httpClientFactory;
    private readonly ILogger<UpdateCheckService> _log;
    private readonly object _gate = new();
    private UpdateCheckResult? _cache;
    private DateTime _cacheUtc = DateTime.MinValue;
    private static readonly TimeSpan CacheTtl = TimeSpan.FromHours(1);

    public UpdateCheckService(IHttpClientFactory httpClientFactory, ILogger<UpdateCheckService> log)
    {
        _httpClientFactory = httpClientFactory;
        _log = log;
    }

    public string InstalledVersion => NormalizeVersion(GetInstalledVersionRaw());

    public async Task<UpdateCheckResult> CheckAsync(CancellationToken ct = default)
    {
        lock (_gate)
        {
            if (_cache is not null && DateTime.UtcNow - _cacheUtc < CacheTtl)
            {
                return _cache;
            }
        }

        try
        {
            var client = _httpClientFactory.CreateClient("github");
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestReleaseUri);
            req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/vnd.github+json"));
            req.Headers.UserAgent.ParseAdd("ExLlamaSharp-UpdateCheck");
            req.Headers.TryAddWithoutValidation("X-GitHub-Api-Version", "2022-11-28");

            using var resp = await client.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                _log.LogDebug("GitHub release check returned {Status}", (int)resp.StatusCode);
                return Cache(UpdateCheckResult.Unavailable(InstalledVersion, $"GitHub HTTP {(int)resp.StatusCode}"));
            }

            await using var stream = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
            var release = await JsonSerializer.DeserializeAsync<GitHubReleaseDto>(stream, cancellationToken: ct)
                .ConfigureAwait(false);
            if (release is null || string.IsNullOrWhiteSpace(release.TagName))
            {
                return Cache(UpdateCheckResult.Unavailable(InstalledVersion, "Empty release payload"));
            }

            var remoteTag = NormalizeVersion(release.TagName);
            var asset = release.Assets?.FirstOrDefault(a =>
                string.Equals(a.Name, SetupAssetName, StringComparison.OrdinalIgnoreCase));

            var downloadUrl = asset?.BrowserDownloadUrl
                ?? $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest/download/{SetupAssetName}";
            var releaseUrl = string.IsNullOrWhiteSpace(release.HtmlUrl)
                ? $"https://github.com/{GitHubOwner}/{GitHubRepo}/releases/latest"
                : release.HtmlUrl!;

            DateTimeOffset? assetUpdated = null;
            if (!string.IsNullOrWhiteSpace(asset?.UpdatedAt)
                && DateTimeOffset.TryParse(asset.UpdatedAt, CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind, out var parsed))
            {
                assetUpdated = parsed;
            }

            var installed = InstalledVersion;
            var newerVersion = IsRemoteNewer(installed, remoteTag);
            var assetNewerThanBuild = assetUpdated is not null
                && GetInstalledBuildUtc() is DateTime buildUtc
                && assetUpdated.Value.UtcDateTime > buildUtc.AddMinutes(5);

            var available = newerVersion || assetNewerThanBuild;
            var reason = available
                ? (newerVersion ? "newer_version" : "installer_updated")
                : "up_to_date";

            return Cache(new UpdateCheckResult(
                Checked: true,
                UpdateAvailable: available,
                Reason: reason,
                InstalledVersion: installed,
                LatestVersion: remoteTag,
                ReleaseUrl: releaseUrl,
                DownloadUrl: downloadUrl,
                AssetUpdatedAt: assetUpdated?.UtcDateTime,
                Message: available
                    ? (newerVersion
                        ? $"Version {remoteTag} is available (installed {installed})."
                        : $"A newer installer is available for {remoteTag}.")
                    : $"You are on the latest release ({installed})."));
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or IOException)
        {
            _log.LogDebug(ex, "Update check failed");
            return Cache(UpdateCheckResult.Unavailable(InstalledVersion, ex.Message));
        }
    }

    private UpdateCheckResult Cache(UpdateCheckResult result)
    {
        lock (_gate)
        {
            _cache = result;
            _cacheUtc = DateTime.UtcNow;
            return result;
        }
    }

    private static string GetInstalledVersionRaw()
    {
        var asm = Assembly.GetExecutingAssembly();
        var info = asm.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion;
        if (!string.IsNullOrWhiteSpace(info))
        {
            var plus = info.IndexOf('+');
            return plus > 0 ? info[..plus] : info;
        }

        return asm.GetName().Version?.ToString() ?? "0.0.0";
    }

    private static DateTime? GetInstalledBuildUtc()
    {
        try
        {
            var path = Assembly.GetExecutingAssembly().Location;
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
            {
                return File.GetLastWriteTimeUtc(path);
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    internal static string NormalizeVersion(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return "0.0.0";
        }

        var s = raw.Trim();
        if (s.StartsWith('v') || s.StartsWith('V'))
        {
            s = s[1..];
        }

        var dash = s.IndexOf('-');
        if (dash > 0)
        {
            s = s[..dash];
        }

        return s;
    }

    internal static bool IsRemoteNewer(string installed, string remote)
    {
        if (!Version.TryParse(PadVersion(installed), out var localVer))
        {
            localVer = new Version(0, 0, 0);
        }

        if (!Version.TryParse(PadVersion(remote), out var remoteVer))
        {
            return false;
        }

        return remoteVer > localVer;
    }

    private static string PadVersion(string v)
    {
        var parts = v.Split('.', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length switch
        {
            1 => $"{parts[0]}.0.0",
            2 => $"{parts[0]}.{parts[1]}.0",
            _ => string.Join('.', parts.Take(4)),
        };
    }

    private sealed class GitHubReleaseDto
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("html_url")]
        public string? HtmlUrl { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAssetDto>? Assets { get; set; }
    }

    private sealed class GitHubAssetDto
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }

        [JsonPropertyName("updated_at")]
        public string? UpdatedAt { get; set; }
    }
}

public sealed record UpdateCheckResult(
    bool Checked,
    bool UpdateAvailable,
    string Reason,
    string InstalledVersion,
    string? LatestVersion,
    string? ReleaseUrl,
    string? DownloadUrl,
    DateTime? AssetUpdatedAt,
    string Message)
{
    public static UpdateCheckResult Unavailable(string installed, string detail) =>
        new(false, false, "unavailable", installed, null, null, null, null, detail);
}