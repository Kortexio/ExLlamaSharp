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
    public bool? HasSafetensors { get; set; }
    public bool? HasTokenizer { get; set; }
    public bool? HasConfig { get; set; }
}

public sealed class HuggingFaceRevisionInfo
{
    public string RepoId { get; init; } = "";
    public string Revision { get; init; } = "main";
    public long BytesTotal { get; init; }
    public string? ParameterLabel { get; init; }
    public bool HasSafetensors { get; init; }
    public bool HasTokenizer { get; init; }
    public bool HasConfig { get; init; }
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

    /// <summary>Minimum total tree size after revision resolve (excludes README-only clones).</summary>
    public const long MinLoadableBytes = 40L * 1024 * 1024;

    public async Task<IReadOnlyList<HuggingFaceModelHit>> SearchAsync(
        string query,
        int limit = 40,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(query))
        {
            query = "exl3";
        }
        else if (!query.Contains("exl3", StringComparison.OrdinalIgnoreCase))
        {
            // Bias HF search toward EXL3 repos even when the user types "qwen", "llama", etc.
            query = $"{query.Trim()} exl3";
        }

        limit = Math.Clamp(limit, 1, 80);
        // Oversample: many EXL3 hits are drafts / incomplete and get filtered client-side.
        var fetchLimit = Math.Clamp(limit * 3, limit, 100);
        var url =
            $"https://huggingface.co/api/models?search={Uri.EscapeDataString(query)}" +
            $"&sort=downloads&direction=-1&limit={fetchLimit}";

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
            var hit = new HuggingFaceModelHit
            {
                RepoId = id,
                DisplayName = display,
                Downloads = downloads,
                PipelineTag = tag,
                Tags = tags ?? "",
                ParameterLabel = InferParameterLabel(id) ?? InferParameterLabel(tags),
            };

            if (!IsStandaloneExl3Candidate(hit))
            {
                continue;
            }

            hits.Add(hit);
            if (hits.Count >= limit)
            {
                break;
            }
        }

        return hits;
    }

    /// <summary>
    /// Metadata gate for Library search: EXL3 chat/VLM targets only (no drafts / DFlash / wrong formats).
    /// </summary>
    public static bool IsStandaloneExl3Candidate(HuggingFaceModelHit hit)
    {
        var id = hit.RepoId ?? "";
        var display = hit.DisplayName ?? "";
        var tags = hit.Tags ?? "";
        var haystack = $"{id} {display} {tags}";

        if (!LooksLikeExl3(haystack))
        {
            return false;
        }

        if (IsExcludedDraftOrAuxiliary(haystack, tags))
        {
            return false;
        }

        if (!IsAllowedPipeline(hit.PipelineTag))
        {
            return false;
        }

        return true;
    }

    /// <summary>
    /// After tree enrichment: require real EXL3 weights + tokenizer (drafts often lack a tokenizer).
    /// Hits still pending enrichment (null flags) pass so the UI can show them while sizes load.
    /// </summary>
    public static bool PassesWeightGate(HuggingFaceModelHit hit)
    {
        if (hit.HasSafetensors is null && hit.HasTokenizer is null && hit.HasConfig is null)
        {
            return true;
        }

        if (hit.HasSafetensors != true || hit.HasTokenizer != true || hit.HasConfig != true)
        {
            return false;
        }

        if (hit.SizeBytes is long size && size > 0 && size < MinLoadableBytes)
        {
            return false;
        }

        return true;
    }

    public static IReadOnlyList<HuggingFaceModelHit> FilterLoadableHits(IEnumerable<HuggingFaceModelHit> hits) =>
        hits.Where(h => IsStandaloneExl3Candidate(h) && PassesWeightGate(h)).ToList();

    private static bool LooksLikeExl3(string haystack) =>
        haystack.Contains("exl3", StringComparison.OrdinalIgnoreCase);

    private static readonly string[] DraftNameNeedles =
    [
        "dflash",
        "dflash2",
        "draft-model",
        "-draft-",
        "_draft_",
        "-draft_",
        "_draft-",
        "/draft-",
        "mtp-draft",
        "speculative-draft",
        "drafter",
    ];

    private static readonly string[] DraftTagNeedles =
    [
        "draft-model",
        "dflash",
        "dflash2",
        "block-diffusion",
    ];

    private static bool IsExcludedDraftOrAuxiliary(string haystack, string tags)
    {
        foreach (var needle in DraftNameNeedles)
        {
            if (haystack.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        foreach (var needle in DraftTagNeedles)
        {
            if (tags.Contains(needle, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        // Name ends with -draft / _draft (common HF convention)
        if (Regex.IsMatch(haystack, @"[-_]draft(?:\b|[-_]|\d)", RegexOptions.IgnoreCase))
        {
            return true;
        }

        // Wrong primary format in the repo id (even if the card mentions exl3)
        if (Regex.IsMatch(haystack, @"(^|[\s/])[^\s]*gguf[^\s]*", RegexOptions.IgnoreCase)
            || Regex.IsMatch(haystack, @"[-_]exl2(?:[-_.]|$)", RegexOptions.IgnoreCase)
            || haystack.Contains("-GGUF", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    private static readonly HashSet<string> AllowedPipelines = new(StringComparer.OrdinalIgnoreCase)
    {
        "text-generation",
        "image-text-to-text",
        "any-to-any",
        "conversational",
    };

    private static readonly HashSet<string> BlockedPipelines = new(StringComparer.OrdinalIgnoreCase)
    {
        "text-to-image",
        "image-to-image",
        "text-to-audio",
        "text-to-speech",
        "automatic-speech-recognition",
        "feature-extraction",
        "sentence-similarity",
        "fill-mask",
        "token-classification",
        "translation",
        "summarization",
        "reinforcement-learning",
    };

    private static bool IsAllowedPipeline(string? pipelineTag)
    {
        if (string.IsNullOrWhiteSpace(pipelineTag))
        {
            return true;
        }

        if (BlockedPipelines.Contains(pipelineTag))
        {
            return false;
        }

        if (AllowedPipelines.Contains(pipelineTag))
        {
            return true;
        }

        // Unknown tags: keep only if not obviously non-LLM
        return !pipelineTag.Contains("image", StringComparison.OrdinalIgnoreCase)
            || pipelineTag.Contains("text", StringComparison.OrdinalIgnoreCase);
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
                hit.HasSafetensors = info.HasSafetensors;
                hit.HasTokenizer = info.HasTokenizer;
                hit.HasConfig = info.HasConfig;
            }
            catch (Exception ex)
            {
                hit.SizeBytes ??= 0;
                hit.HasSafetensors ??= false;
                hit.HasTokenizer ??= false;
                hit.HasConfig ??= false;
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

        var tree = await AnalyzeTreeAsync(repoId, revision, ct).ConfigureAwait(false);
        var info = new HuggingFaceRevisionInfo
        {
            RepoId = repoId,
            Revision = revision,
            BytesTotal = tree.BytesTotal,
            ParameterLabel = InferParameterLabel(repoId),
            HasSafetensors = tree.HasSafetensors,
            HasTokenizer = tree.HasTokenizer,
            HasConfig = tree.HasConfig,
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

    private sealed record TreeAnalysis(long BytesTotal, bool HasSafetensors, bool HasTokenizer, bool HasConfig);

    private async Task<TreeAnalysis> AnalyzeTreeAsync(string repoId, string revision, CancellationToken ct)
    {
        var encodedRepo = string.Join('/', repoId.Split('/', StringSplitOptions.RemoveEmptyEntries).Select(Uri.EscapeDataString));
        var url =
            $"https://huggingface.co/api/models/{encodedRepo}/tree/{Uri.EscapeDataString(revision)}?recursive=true";
        using var res = await SendHfGetAsync(url, ct).ConfigureAwait(false);
        if (!res.IsSuccessStatusCode)
        {
            _logger.LogWarning("HF tree failed for {Repo}@{Rev}: {Status}", repoId, revision, (int)res.StatusCode);
            return new TreeAnalysis(0, false, false, false);
        }

        using var doc = JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct).ConfigureAwait(false));
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
        {
            return new TreeAnalysis(0, false, false, false);
        }

        long total = 0;
        var hasSafetensors = false;
        var hasTokenizer = false;
        var hasConfig = false;
        foreach (var el in doc.RootElement.EnumerateArray())
        {
            var type = el.TryGetProperty("type", out var t) ? t.GetString() : null;
            if (!string.Equals(type, "file", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var path = el.TryGetProperty("path", out var pathEl) ? pathEl.GetString() ?? "" : "";
            var fileName = Path.GetFileName(path);
            if (el.TryGetProperty("size", out var sizeEl) && sizeEl.TryGetInt64(out var size) && size > 0)
            {
                total += size;
            }

            if (fileName.EndsWith(".safetensors", StringComparison.OrdinalIgnoreCase))
            {
                hasSafetensors = true;
            }
            else if (fileName.Equals("tokenizer.json", StringComparison.OrdinalIgnoreCase)
                     || fileName.Equals("tokenizer.model", StringComparison.OrdinalIgnoreCase)
                     || fileName.Equals("tokenizer_config.json", StringComparison.OrdinalIgnoreCase))
            {
                hasTokenizer = true;
            }
            else if (fileName.Equals("config.json", StringComparison.OrdinalIgnoreCase))
            {
                hasConfig = true;
            }
        }

        return new TreeAnalysis(total, hasSafetensors, hasTokenizer, hasConfig);
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
