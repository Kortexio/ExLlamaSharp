using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;

namespace ExLlamaSharp.Server.Tests;

public class OpenAiApiTests : IClassFixture<WebApplicationFactory<Program>>
{
    private readonly HttpClient _client;

    public OpenAiApiTests(WebApplicationFactory<Program> factory)
    {
        _client = factory
            .WithWebHostBuilder(builder => builder.UseSetting("ExLlamaSharp:ForceMockEngine", "true"))
            .CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", "sk-exllamasharp-dev");
    }

    [Fact]
    public async Task Health_returns_ok()
    {
        var response = await _client.GetAsync("/health");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var json = await response.Content.ReadAsStringAsync();
        Assert.Contains("status", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task About_returns_version()
    {
        var response = await _client.GetAsync("/api/v1/about");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        Assert.True(
            doc.RootElement.TryGetProperty("version", out _) ||
            doc.RootElement.TryGetProperty("Version", out _));
    }

    [Fact]
    public async Task Models_list_with_dev_key()
    {
        var response = await _client.GetAsync("/v1/models");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Chat_completions_mock_engine()
    {
        var payload = new
        {
            model = "mock",
            messages = new[]
            {
                new { role = "user", content = "Hello" },
            },
            max_tokens = 8,
            stream = false,
        };

        var response = await _client.PostAsJsonAsync("/v1/chat/completions", payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("choices", body, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Chat_completions_stream_emits_multiple_chunks_before_done()
    {
        var payload = new
        {
            model = "mock",
            messages = new[]
            {
                new { role = "user", content = "Please write a longer mock reply for streaming" },
            },
            max_tokens = 40,
            stream = true,
        };

        var response = await _client.PostAsJsonAsync("/v1/chat/completions", payload);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("text/event-stream", response.Content.Headers.ContentType?.MediaType ?? "");
        var sse = await response.Content.ReadAsStringAsync();
        Assert.Contains("data:", sse);
        Assert.Contains("[DONE]", sse);

        var dataLines = sse
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Count(l => l.StartsWith("data:", StringComparison.Ordinal));
        // role + at least two content chunks + stop + [DONE]
        Assert.True(dataLines >= 4, $"expected multiple SSE frames, got {dataLines}: {sse}");
    }
}
