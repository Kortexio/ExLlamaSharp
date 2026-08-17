using ExLlamaSharp.Engine;

namespace ExLlamaSharp.Tests;

/// <summary>
/// Quick stress smoke: 100 concurrent mock engine jobs.
/// </summary>
public class StressSmokeTests
{
    [Fact]
    public async Task One_hundred_concurrent_mock_jobs_complete()
    {
        await using var engine = ExLlamaEngine.Create(forceMock: true);
        await engine.LoadAsync("mock://stress-smoke");
        engine.Start();

        const int count = 100;
        var tasks = Enumerable.Range(0, count).Select(i => engine.SubmitAsync(new CompletionRequest
        {
            Prompt = $"stress-{i}",
            MaxNewTokens = 4,
            Temperature = 0f,
        }));

        var results = await Task.WhenAll(tasks);

        Assert.Equal(count, results.Length);
        Assert.All(results, r =>
        {
            Assert.False(string.IsNullOrEmpty(r.Text));
            Assert.True(r.TokenIds.Length > 0);
        });
    }
}
