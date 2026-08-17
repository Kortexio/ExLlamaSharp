using System.Collections.Concurrent;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Extensions.Configuration;

namespace ExLlamaSharp.Server.Services;

public sealed class HuggingFaceModelHit
{
    public string RepoId { get; set; } = "";
    public string DisplayName { get; set; } = "";
    public int Downloads { get; set; }
    public string? PipelineTag { get; set; }
    public string Tags { get; set; } = "";
    public string? ParameterLabel { get; set; }
    public long? SizeBytes { get; set; }
}

public sealed class HuggingFaceRevisionInfo
{
    public string RepoId { get; init; } = "";
    public string Revision { get; init; } = "main";
    public long BytesTotal { get; init; }
    public string? ParameterLabel { get; init; }
}

public sealed class HuggingFaceCatalogService
{
    private static readonly Regex ParameterLabelRegex = new(
        @"(?<!\d)(\d+(?:\.\d+)?(?:x\d+(?:\.\d+)?)?[BMbm])(?![A-Za-z0-9])",
        RegexOptions.Compiled);

    private readonly ConcurrentDictionary<string, HuggingFaceRevisionInfo> _revisionCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly IHttpClientFactory _http;
    private readonly IConfiguration _config;
    private readonly ILogger<HuggingFaceCatalogService> _logger;

    public HuggingFaceCatalogService(
        IHttpClientFactory http,
        IConfiguration config,
        ILogger<HuggingFaceCatalogService> logger)
    {
        _http = http;
        _config = config;
        _logger = logger;
    }

    public static string? ResolveToken(IConfiguration config)
    {
        var env = Environment.GetEnvironmentVariable("HF_TOKEN")
            ?? Environment.GetEnvironmentVariable("HUGGING_FACE_HUB_TOKEN");
        if (!string.IsNullOrWhiteSpace(env))
        {
            return env.Trim();
        }

        var cfg = config["ExLlamaSharp:HuggingFaceToken"];
        if (!string.IsNullOrWhiteSpace(cfg))
        {
            return cfg.Trim();
        }

        var file = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ExLlamaSharp",
            "hf-token.txt");
        if (File.Exists(file))
        {
            var text = File.ReadAllText(file).Trim();
            if (!string.IsNullOrWhiteSpace(text))
            {
                return text;
            }
        }

        return null;
    }

    public async Task<IReadOnlyList<HuggingFaceModelHit>> SearchAsync(
        string query,
        int limit = 40,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            query = "exl3";
        }

        limit = Math.Clamp(limit, 1, 80);
        var url =
            $"https://huggingface.co/api/models?search={Uri.EscapeDataString(query)}" +
            $"&sort=downloads&direction=-1&limit={limit}";

        var client = _http.CreateClient("huggingface");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.UserAgent.ParseAdd("ExLlamaSharp/1.0");
        var token = ResolveToken(_config);
        if (!string.IsNullOrWhiteSpace(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        using var res = await client.SendAsync(req, ct).ConfigureAwait(false);
        var body = await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            _logger.LogWarning("Hugging Face search failed {Status}: {Body}", (int)res.StatusCode, body);
            throw new InvalidOperationException(
                $"Hugging Face returned {(int)res.StatusCode}. " +
                (res.StatusCode == System.Net.HttpStatusCode.TooManyRequests
                    ? "Rate limited — set HF_TOKEN (Settings or env) for higher limits."
                    : body.Length > 240 ? body[..240] : body));
        }

        using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(body) ? "[]" : body);
        var hits = new List<HuggingFaceModelHit>();
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var id = el.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
            if (string.IsNullOrWhiteSpace(id))
            {
                continue;
            }

            var downloads = el.TryGetProperty("downloads", out var d) && d.TryGetInt32(out var n) ? n : 0;
            var tag = el.TryGetProperty("pipeline_tag", out var p) ? p.GetString() : null;
            var tags = "";
            if (el.TryGetProperty("tags", out var t) && t.ValueKind == JsonValueKind.Array)
            {
                tags = string.Join(",", t.EnumerateArray().Select(x => x.GetString()).Where(s => !string.IsNullOrWhiteSpace(s)));
            }

            var display = id.Contains('/', StringComparison.Ordinal) ? id[(id.LastIndexOf('/') + 1)..] : id;
            hits.Add(new HuggingFaceModelHit
            {
                RepoId = id,
                DisplayName = display,
                Downloads = downloads,
                PipelineTag = tag,
                Tags = tags ?? "",
                ParameterLabel = InferParameterLabel(id) ?? InferParameterLabel(tags),
            });
        }

        return hits;
    }

    public async Task EnrichHitsAsync(IEnumerable<HuggingFaceModelHit> hits, CancellationToken ct = default)
    {
        using var gate = new SemaphoreSlim(4);
        var tasks = hits.Select(async hit =>
        {
            await gate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                var revision = await ResolveRevisionAsync(hit.RepoId, null, ct).ConfigureAwait(false);
                var info = await GetRevisionInfoAsync(hit.RepoId, revision, ct).ConfigureAwait(false);
                hit.SizeBytes = info.BytesTotal;
                hit.ParameterLabel ??= info.ParameterLabel;
            }
            catch (Exception ex)
            {
                hit.SizeBytes ??= 0;
                _logger.LogDebug(ex, "Could not enrich HF hit {Repo}", hit.RepoId);
            }
            finally
            {
                gate.Release();
            }
        });
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    public async Task<HuggingFaceRevisionInfo> GetRevisionInfoAsync(
        string repoId,
        string revision,
        CancellationToken ct = default)
    {
        var key = $"{repoId}@{revision}";
        if (_revisionCache.TryGetValue(key, out var cached))
        {
            return cached;
        }

        var bytes = await SumTreeBytesAsync(repoId, revision, ct).ConfigureAwait(false);
        var info = new HuggingFaceRevisionInfo
        {
            RepoId = repoId,
            Revision = revision,
            BytesTotal = bytes,
            ParameterLabel = InferParameterLabel(repoId),
        };
        _revisionCache[key] = info;
        return info;
    }

    public static string? InferParameterLabel(string? text)
    {
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        var matches = ParameterLabelRegex.Matches(text);
        if (matches.Count == 0)
        {
            return null;
        }

        return matches[^1].Value.ToUpperInvariant();
    }

    public static string FormatBytes(long bytes)
    {
        if (bytes < 0)
        {
            return "—";
        }

        const double kb = 1024;
        const double mb = kb * 1024;
        const double gb = mb * 1024;
        if (bytes >= gb)
        {
            return $"{bytes / gb:0.00} GB";
        }

        if (bytes >= mb)
        {
            return $"{bytes / mb:0.0} MB";
        }

        if (bytes >= kb)
        {
            return $"{bytes / kb:0} KB";
        }

        return $"{bytes} B";
    }

    /// <summary>
    /// turboderp EXL3 repos keep weights on branches like 4.00bpw, not on main.
    /// </summary>
    public async Task<string> ResolveRevisionAsync(string repoId, string? requested, CancellationToken ct = default)
    {
        var branches = await ListBranchesAsync(repoId, ct).ConfigureAwait(false);
        if (branches.Count == 0)
        {
            return string.IsNullOrWhiteSpace(requested) ? "main" : requested;
        }

        if (!string.IsNullOrWhiteSpace(requested)
            && !requested.Equals("main", StringComparison.OrdinalIgnoreCase)
            && branches.Contains(requested, StringComparer.OrdinalIgnoreCase))
        {
            return branches.First(b => b.Equals(requested, StringComparison.OrdinalIgnoreCase));
        }

        foreach (var preferred in new[] { "4.00bpw", "4.0bpw", "3.50bpw", "3.5bpw", "5.00bpw", "3.00bpw" })
        {
            var match = branches.FirstOrDefault(b => b.Equals(preferred, StringComparison.OrdinalIgnoreCase));
            if (match is not null)
            {
                return match;
            }
        }

        var anyBpw = branches
            .Where(b => b.Contains("bpw", StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(b => b, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
        return anyBpw ?? (branches.Contains("main") ? "main" : branches[0]);
    }

    public async Task<IReadOnlyList<string>> ListBranchesAsync(string repoId, CancellationToken ct = default)
    {
        using var res = await SendHfGetAsync($"https://huggingface.co/api/models/{repoId}/refs", ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            _logger.LogWarning("HF refs failed for {Repo}: {Status}", repoId, (int)res.StatusCode);
            return [];
        }

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        var names = new List<string>();
        if (doc.RootElement.TryGetProperty("branches", out var branches))
        {
            foreach (var b in branches.EnumerateArray())
            {
                if (b.TryGetProperty("name", out var n))
                {
                    var name = n.GetString();
                    if (!string.IsNullOrWhiteSpace(name))
                    {
                        names.Add(name);
                    }
                }
            }
        }

        return names;
    }

    private async Task<long> SumTreeBytesAsync(string repoId, string revision, CancellationToken ct)
    {
        var encodedRepo = string.Join('/', repoId.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var url =
            $"https://huggingface.co/api/models/{encodedRepo}/tree/{Uri.EscapeDataString(revision)}?recursive=true";
        using var res = await SendHfGetAsync(url, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            _logger.LogWarning("HF tree failed for {Repo}@{Rev}: {Status}", repoId, revision, (int)res.StatusCode);
            return 0;
        }

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return 0;
        }

        long total = 0;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (el.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var size) && size > 0)
            {
                total += size;
            }
        }

        return total;
    }

    private async Task<HttpResponseMessage> SendHfGetAsync(string url, CancellationToken ct)
    {
        var client = _http.CreateClient("huggingface");
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        req.Headers.UserAgent.ParseAdd("ExLlamaSharp/1.0");
        var token = ResolveToken(_config);
        if (!string.IsNullOrWhiteSpace(token))
        {
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        }

        return await client.SendAsync(req, ct).ConfigureAwait(false);
    }
}
