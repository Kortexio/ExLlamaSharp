using ExLlamaSharp.Chat;
using ExLlamaSharp.Engine;
using ExLlamaSharp.Tokenizer;

namespace ExLlamaSharp.Tests;

public class MockEngineParityTests
{
    [Fact]
    public async Task Greedy_generation_is_deterministic_for_same_prompt()
    {
        await using var a = ExLlamaEngine.Create(forceMock: true);
        await using var b = ExLlamaEngine.Create(forceMock: true);
        await a.LoadAsync("mock://parity");
        await b.LoadAsync("mock://parity");
        a.Start();
        b.Start();

        var req = new CompletionRequest
        {
            Prompt = "Hello world",
            MaxNewTokens = 16,
            Temperature = 0f,
        };

        var ra = await a.SubmitAsync(req);
        var rb = await b.SubmitAsync(req);

        Assert.Equal(ra.Text, rb.Text);
        Assert.Equal(ra.TokenIds, rb.TokenIds);
    }

    [Fact]
    public void Tokenizer_encode_is_deterministic()
    {
        var tokenizer = new SimpleTokenizer();
        var ids1 = tokenizer.Encode("hello world");
        var ids2 = tokenizer.Encode("hello world");
        Assert.NotEmpty(ids1);
        Assert.Equal(ids1, ids2);
        Assert.False(string.IsNullOrWhiteSpace(tokenizer.Decode(ids1)));
    }

    [Fact]
    public void StripSpecialTokens_cuts_llama_leak()
    {
        var raw = "Hello<|eot_id|>so<|end_header_id|>more";
        Assert.Equal("Hello", ChatTemplate.StripSpecialTokens(raw));
    }

    [Fact]
    public void Llama3_chat_template_includes_roles()
    {
        var messages = new List<ChatMessage>
        {
            new() { Role = ChatRole.System, Content = "You are helpful." },
            new() { Role = ChatRole.User, Content = "Hi" },
        };
        var formatted = ChatTemplate.Format(messages);
        Assert.Contains("system", formatted);
        Assert.Contains("user", formatted);
        Assert.Contains("Hi", formatted);
    }

    [Fact]
    public async Task Concurrent_jobs_complete()
    {
        await using var engine = ExLlamaEngine.Create(forceMock: true);
        await engine.LoadAsync("mock://bench");
        engine.Start();

        var tasks = Enumerable.Range(0, 32).Select(i => engine.SubmitAsync(new CompletionRequest
        {
            Prompt = $"job-{i}",
            MaxNewTokens = 8,
            Temperature = 0f,
        }));

        var results = await Task.WhenAll(tasks);
        Assert.Equal(32, results.Length);
        Assert.All(results, r => Assert.False(string.IsNullOrEmpty(r.Text)));
    }

    [Fact]
    public async Task Default_stream_yields_one_terminal_delta()
    {
        IInferenceEngine engine = ExLlamaEngine.Create(forceMock: true);
        await using (engine)
        {
            await engine.LoadAsync("mock://stream");
            engine.Start();

            Assert.False(engine.SupportsStreaming);
            var n = 0;
            await foreach (var delta in engine.SubmitStreamAsync(new CompletionRequest
            {
                Prompt = "stream-default",
                MaxNewTokens = 8,
                Temperature = 0f,
            }))
            {
                n++;
                Assert.True(delta.Eos);
                Assert.False(string.IsNullOrEmpty(delta.Text));
            }

            Assert.Equal(1, n);
        }
    }
}
