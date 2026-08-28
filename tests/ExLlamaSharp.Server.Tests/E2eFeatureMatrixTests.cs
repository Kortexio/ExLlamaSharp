using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using ExLlamaSharp.Server.Auth;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ExLlamaSharp.Server.Tests;

[CollectionDefinition("E2eSerial", DisableParallelization = true)]
public sealed class E2eSerialCollection : ICollectionFixture<E2eHostFixture>;

/// <summary>
/// Shared host with isolated <c>EXLLAMASHARP_DATA_ROOT</c> and seed key <c>sk-exllamasharp-dev</c>.
/// </summary>
public sealed class E2eHostFixture : IDisposable
{
    public const string SeedKey = "sk-exllamasharp-dev";

    public string DataRoot { get; }
    public WebApplicationFactory<Program> Factory { get; }

    public E2eHostFixture()
    {
        DataRoot = Path.Combine(Path.GetTempPath(), "ExLlamaSharp-e2e-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DataRoot);
        Environment.SetEnvironmentVariable("EXLLAMASHARP_DATA_ROOT", DataRoot);

        Factory = new WebApplicationFactory<Program>().WithWebHostBuilder(builder =>
        {
            builder.UseSetting("ExLlamaSharp:ForceMockEngine", "true");
        });
        // Force host start so DbInitializer seeds before tests run.
        using var warmup = Factory.CreateClient();
        _ = warmup.GetAsync("/health").GetAwaiter().GetResult();
    }

    public HttpClient CreateAdminClient()
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", SeedKey);
        return client;
    }

    public HttpClient CreateAnonClient() => Factory.CreateClient();

    public void Dispose()
    {
        Factory.Dispose();
        try
        {
            if (Directory.Exists(DataRoot))
            {
                Directory.Delete(DataRoot, recursive: true);
            }
        }
        catch
        {
            // temp cleanup best-effort
        }

        Environment.SetEnvironmentVariable("EXLLAMASHARP_DATA_ROOT", null);
    }
}

/// <summary>
/// End-to-end feature matrix against mock engine (see tests/e2e/PLAN.md).
/// </summary>
[Collection("E2eSerial")]
public sealed class E2eFeatureMatrixTests
{
    private readonly E2eHostFixture _fixture;
    private readonly HttpClient _admin;
    private readonly HttpClient _anon;
    private readonly List<Finding> _findings = [];

    public E2eFeatureMatrixTests(E2eHostFixture fixture)
    {
        _fixture = fixture;
        _admin = fixture.CreateAdminClient();
        _anon = fixture.CreateAnonClient();
    }

    [Fact]
    public async Task A_Ops_health_ready_metrics()
    {
        await ExpectOk(_anon, HttpMethod.Get, "/health", "A.health");

        var ready = await _anon.GetAsync("/ready");
        Assert.True(
            ready.StatusCode is HttpStatusCode.OK or HttpStatusCode.ServiceUnavailable,
            $"ready unexpected {(int)ready.StatusCode}");
        Note("A.ready", ready.IsSuccessStatusCode ? "pass" : "warn",
            ready.IsSuccessStatusCode ? "ready" : "503 until model loaded (acceptable)");

        var metrics = await _anon.GetAsync("/metrics");
        Assert.Equal(HttpStatusCode.OK, metrics.StatusCode);
        var body = await metrics.Content.ReadAsStringAsync();
        Assert.Contains("exllamasharp_", body);
        Note("A.metrics", "pass", "prometheus text");
    }

    [Fact]
    public async Task B_OpenAi_surface()
    {
        await ExpectStatus(_anon, HttpMethod.Get, "/v1/models", HttpStatusCode.Unauthorized, "B.models_no_auth");

        await ExpectOk(_admin, HttpMethod.Get, "/v1/models", "B.models");
        await ExpectOk(_admin, HttpMethod.Get, "/v1/metrics", "B.engine_metrics");

        var chat = await _admin.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "mock",
            messages = new[] { new { role = "user", content = "ping e2e" } },
            max_tokens = 16,
            stream = false,
        });
        Assert.Equal(HttpStatusCode.OK, chat.StatusCode);
        var chatBody = await chat.Content.ReadAsStringAsync();
        Assert.Contains("choices", chatBody, StringComparison.OrdinalIgnoreCase);
        Note("B.chat", "pass", "non-stream");

        var stream = await _admin.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "mock",
            messages = new[] { new { role = "user", content = "stream" } },
            max_tokens = 8,
            stream = true,
        });
        Assert.Equal(HttpStatusCode.OK, stream.StatusCode);
        Assert.Contains("text/event-stream", stream.Content.Headers.ContentType?.MediaType ?? "");
        var sse = await stream.Content.ReadAsStringAsync();
        Assert.Contains("data:", sse);
        Note("B.chat_stream", "pass", "sse");

        var completion = await _admin.PostAsJsonAsync("/v1/completions", new
        {
            model = "mock",
            prompt = "Hello",
            max_tokens = 8,
        });
        Assert.True(
            completion.IsSuccessStatusCode,
            $"completions {(int)completion.StatusCode}: {await completion.Content.ReadAsStringAsync()}");
        Note("B.completions", "pass", "ok");

        var emb = await _admin.PostAsJsonAsync("/v1/embeddings", new
        {
            model = "mock",
            input = "hello embeddings",
        });
        Assert.Equal(HttpStatusCode.OK, emb.StatusCode);
        Note("B.embeddings", "pass", "ok");

        var tok = await _admin.PostAsJsonAsync("/v1/tokenize", new { model = "mock", prompt = "hi" });
        Assert.Equal(HttpStatusCode.OK, tok.StatusCode);
        using var tokDoc = JsonDocument.Parse(await tok.Content.ReadAsStringAsync());
        Assert.True(tokDoc.RootElement.TryGetProperty("tokens", out var tokens));

        var ids = tokens.EnumerateArray().Select(e => e.GetInt32()).Take(4).ToArray();
        var detok = await _admin.PostAsJsonAsync("/v1/detokenize", new { model = "mock", tokens = ids });
        Assert.Equal(HttpStatusCode.OK, detok.StatusCode);
        Note("B.tokenize_detokenize", "pass", "ok");

        var notImpl = await _admin.PostAsJsonAsync("/v1/images/generations", new { prompt = "x" });
        Assert.Equal(HttpStatusCode.NotImplemented, notImpl.StatusCode);
        Note("B.501", "pass", "images/generations");
    }

    [Fact]
    public async Task C_Admin_crud_and_jobs()
    {
        await ExpectOk(_anon, HttpMethod.Get, "/api/v1/about", "C.about_anon");
        await ExpectStatus(_anon, HttpMethod.Get, "/api/v1/settings", HttpStatusCode.Unauthorized, "C.settings_no_auth");

        await ExpectOk(_admin, HttpMethod.Get, "/api/v1/settings", "C.settings_get");
        await ExpectOk(_admin, HttpMethod.Get, "/api/v1/models/library", "C.library");
        await ExpectOk(_admin, HttpMethod.Get, "/api/v1/jobs", "C.jobs");
        await ExpectOk(_admin, HttpMethod.Get, "/api/v1/keys", "C.keys_list");
        await ExpectOk(_admin, HttpMethod.Get, "/api/v1/users", "C.users_list");
        await ExpectOk(_admin, HttpMethod.Get, "/api/v1/moderation/rules", "C.moderation_list");

        var load = await _admin.PostAsJsonAsync("/api/v1/models/load", new { path = "mock://default", alias = "mock" });
        Assert.True(load.IsSuccessStatusCode, $"load {(int)load.StatusCode}: {await load.Content.ReadAsStringAsync()}");
        Note("C.load_mock", "pass", "mock://default");

        var unload = await _admin.PostAsJsonAsync("/api/v1/models/unload", new { });
        Assert.True(unload.IsSuccessStatusCode);
        Note("C.unload", "pass", "ok");

        // Reload so subsequent OpenAI calls on shared host stay healthy
        var reload = await _admin.PostAsJsonAsync("/api/v1/models/load", new { path = "mock://default" });
        Assert.True(reload.IsSuccessStatusCode);

        var createKey = await _admin.PostAsJsonAsync("/api/v1/keys", new
        {
            name = "e2e-key",
            scopes = "chat,completions",
            rpm = 30,
            tpm = 50_000,
        });
        Assert.Equal(HttpStatusCode.Created, createKey.StatusCode);
        using var keyDoc = JsonDocument.Parse(await createKey.Content.ReadAsStringAsync());
        Assert.True(keyDoc.RootElement.TryGetProperty("key", out var keyEl), "create key should return plaintext once");
        var plainKey = keyEl.GetString();
        Assert.False(string.IsNullOrWhiteSpace(plainKey));
        Assert.True(keyDoc.RootElement.TryGetProperty("id", out var keyIdEl));
        var keyId = keyIdEl.GetGuid();
        Note("C.keys_create", "pass", "plaintext once");

        using var chatClient = _fixture.Factory.CreateClient();
        chatClient.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", plainKey);
        var scopedChat = await chatClient.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "mock",
            messages = new[] { new { role = "user", content = "scoped" } },
            max_tokens = 4,
        });
        Assert.Equal(HttpStatusCode.OK, scopedChat.StatusCode);
        Note("C.keys_scope_chat", "pass", "chat scope ok");

        var adminDenied = await chatClient.GetAsync("/api/v1/settings");
        Assert.Equal(HttpStatusCode.Forbidden, adminDenied.StatusCode);
        Note("C.keys_scope_admin_denied", "pass", "403");

        var delKey = await _admin.DeleteAsync($"/api/v1/keys/{keyId}");
        Assert.True(delKey.IsSuccessStatusCode, $"delete key {(int)delKey.StatusCode}");
        var revokedChat = await chatClient.PostAsJsonAsync("/v1/chat/completions", new
        {
            model = "mock",
            messages = new[] { new { role = "user", content = "revoked" } },
            max_tokens = 4,
        });
        Assert.Equal(HttpStatusCode.Unauthorized, revokedChat.StatusCode);
        Note("C.keys_revoke", "pass", "401 after revoke");

        var userName = "e2euser_" + Guid.NewGuid().ToString("N")[..8];
        var createUser = await _admin.PostAsJsonAsync("/api/v1/users", new
        {
            username = userName,
            password = "TestPass123!",
            role = "user",
        });
        Assert.Equal(HttpStatusCode.Created, createUser.StatusCode);
        Note("C.users_create", "pass", userName);

        var rule = await _admin.PostAsJsonAsync("/api/v1/moderation/rules", new
        {
            pattern = "forbiddenwordxyz",
            action = "block",
            category = "test",
            enabled = true,
        });
        Assert.True(rule.IsSuccessStatusCode, await rule.Content.ReadAsStringAsync());
        Note("C.moderation_create", "pass", "201/200");

        var pull = await _admin.PostAsJsonAsync("/api/v1/models/pull", new
        {
            repo_id = "org/model-e2e",
        });
        Assert.Equal(HttpStatusCode.Accepted, pull.StatusCode);
        Note("C.pull", "pass", "202");

        var backup = await _admin.PostAsync("/api/v1/backup", null);
        Assert.True(backup.IsSuccessStatusCode, $"backup {(int)backup.StatusCode}");
        Note("C.backup", "pass", "ok");

        using var sseCts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        using var sseReq = new HttpRequestMessage(HttpMethod.Get, "/api/v1/logs/stream");
        sseReq.Headers.Authorization = new AuthenticationHeaderValue("Bearer", E2eHostFixture.SeedKey);
        using var sseResp = await _admin.SendAsync(sseReq, HttpCompletionOption.ResponseHeadersRead, sseCts.Token);
        Assert.Equal(HttpStatusCode.OK, sseResp.StatusCode);
        await using var stream = await sseResp.Content.ReadAsStreamAsync(sseCts.Token);
        using var reader = new StreamReader(stream);
        var line = await reader.ReadLineAsync(sseCts.Token);
        Assert.False(string.IsNullOrWhiteSpace(line), "SSE should emit at least one line (history or live)");
        Note("C.logs_sse", "pass", line!);
    }

    [Fact]
    public async Task D_Auth_negatives()
    {
        await ExpectStatus(_anon, HttpMethod.Get, "/v1/models", HttpStatusCode.Unauthorized, "D.missing_key");
        await ExpectStatus(_anon, HttpMethod.Get, "/api/v1/settings", HttpStatusCode.Unauthorized, "D.admin_missing_key");

        using var bad = _fixture.Factory.CreateClient();
        bad.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", "sk-invalid-not-a-real-key");
        var resp = await bad.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.Unauthorized, resp.StatusCode);
        Note("D.invalid_key", "pass", "401");
    }

    [Fact]
    public async Task E_Ab_tenants_adapters_crud()
    {
        var tenants = await _admin.GetAsync("/api/v1/tenants");
        Assert.Equal(HttpStatusCode.OK, tenants.StatusCode);
        var tenantsBody = await tenants.Content.ReadAsStringAsync();
        Assert.Contains("data", tenantsBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stub", tenantsBody, StringComparison.OrdinalIgnoreCase);
        Note("E.tenants_list", "pass", "real data");

        var ab = await _admin.GetAsync("/api/v1/ab");
        Assert.Equal(HttpStatusCode.OK, ab.StatusCode);
        var abBody = await ab.Content.ReadAsStringAsync();
        Assert.Contains("data", abBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stub", abBody, StringComparison.OrdinalIgnoreCase);
        Note("E.ab_list", "pass", "real data");

        var adapters = await _admin.GetAsync("/api/v1/adapters");
        Assert.Equal(HttpStatusCode.OK, adapters.StatusCode);
        var adaptersBody = await adapters.Content.ReadAsStringAsync();
        Assert.Contains("data", adaptersBody, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("stub", adaptersBody, StringComparison.OrdinalIgnoreCase);
        Note("E.adapters_list", "pass", "real data");

        using var createTenant = new StringContent(
            """{"id":"e2e-tenant","name":"E2E Tenant"}""",
            Encoding.UTF8,
            "application/json");
        var createdTenant = await _admin.PostAsync("/api/v1/tenants", createTenant);
        Assert.Equal(HttpStatusCode.Created, createdTenant.StatusCode);
        Note("E.tenants_create", "pass", "201");
    }

    [Fact]
    public async Task E2_Moderation_blocks_when_enabled()
    {
        using var enable = new StringContent(
            """{"content_moderation_enabled":true}""",
            Encoding.UTF8,
            "application/json");
        var patch = await _admin.SendAsync(new HttpRequestMessage(HttpMethod.Patch, "/api/v1/settings") { Content = enable });
        Assert.True(patch.IsSuccessStatusCode);

        using var ruleBody = new StringContent(
            """{"pattern":"forbiddenwordxyz","action":"block","category":"test","enabled":true}""",
            Encoding.UTF8,
            "application/json");
        var rule = await _admin.PostAsync("/api/v1/moderation/rules", ruleBody);
        Assert.Equal(HttpStatusCode.Created, rule.StatusCode);

        using var chat = new StringContent(
            """{"model":"default","messages":[{"role":"user","content":"please say forbiddenwordxyz now"}]}""",
            Encoding.UTF8,
            "application/json");
        var resp = await _admin.PostAsync("/v1/chat/completions", chat);
        Assert.Equal(HttpStatusCode.BadRequest, resp.StatusCode);
        var body = await resp.Content.ReadAsStringAsync();
        Assert.Contains("content_filter", body, StringComparison.OrdinalIgnoreCase);
        Note("E2.moderation", "pass", "400 content_filter");

        using var disable = new StringContent(
            """{"content_moderation_enabled":false}""",
            Encoding.UTF8,
            "application/json");
        await _admin.SendAsync(new HttpRequestMessage(HttpMethod.Patch, "/api/v1/settings") { Content = disable });
    }

    [Fact]
    public async Task F_Ui_pages_smoke()
    {
        string[] routes =
        [
            "/", "/chat", "/models", "/jobs", "/keys", "/usage", "/team",
            "/adapters", "/dashboard/metrics", "/logs", "/diagnostics", "/admin/tenants",
            "/settings", "/setup", "/api", "/about", "/login",
        ];

        foreach (var route in routes)
        {
            var resp = await _anon.GetAsync(route);
            Assert.True(
                resp.IsSuccessStatusCode,
                $"UI {route} → {(int)resp.StatusCode}");
            var html = await resp.Content.ReadAsStringAsync();
            Assert.False(string.IsNullOrWhiteSpace(html));
            Note("F" + route, "pass", "200");
        }
    }

    [Fact]
    public void G_PasswordHasher_and_ApiKeyHasher_consistency()
    {
        var hashed = PasswordHasher.Hash("secret");
        Assert.True(PasswordHasher.Verify("secret", hashed));
        Assert.False(PasswordHasher.Verify("wrong", hashed));

        var legacy = Convert.ToHexString(
            System.Security.Cryptography.SHA256.HashData(Encoding.UTF8.GetBytes("legacy")));
        Assert.True(PasswordHasher.Verify("legacy", legacy));

        var keyHash = ApiKeyHasher.Hash("sk-test");
        Assert.Equal(keyHash, keyHash.ToLowerInvariant());
        Assert.DoesNotContain(keyHash, c => char.IsUpper(c));
        Note("G.hashers", "pass", "PasswordHasher + ApiKeyHasher");
    }

    private async Task ExpectOk(HttpClient client, HttpMethod method, string path, string id) =>
        await ExpectStatus(client, method, path, HttpStatusCode.OK, id);

    private async Task ExpectStatus(
        HttpClient client,
        HttpMethod method,
        string path,
        HttpStatusCode expected,
        string id)
    {
        using var req = new HttpRequestMessage(method, path);
        var resp = await client.SendAsync(req);
        Assert.Equal(expected, resp.StatusCode);
        Note(id, "pass", $"{method} {path} → {(int)resp.StatusCode}");
    }

    private void Note(string id, string status, string detail) =>
        _findings.Add(new Finding(id, status, detail));

    private sealed record Finding(string Id, string Status, string Detail);
}
