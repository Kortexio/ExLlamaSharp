using ExLlamaSharp.Chat;
using ExLlamaSharp.Engine;

namespace ExLlamaSharp.Tests;

public class StreamingStopFilterTests
{
    [Fact]
    public void Holds_back_partial_marker_then_emits_cut()
    {
        var filter = new StreamingStopFilter();
        Assert.Equal("Hello", filter.Push("Hello<|eo"));
        Assert.Equal("", filter.Push("t_id"));
        Assert.Equal("", filter.Push("|>more"));
        Assert.True(filter.Stopped);
        Assert.Equal("", filter.Flush());
    }

    [Fact]
    public void Flushes_held_suffix_when_it_is_not_a_marker()
    {
        var filter = new StreamingStopFilter();
        Assert.Equal("Hi", filter.Push("Hi<"));
        Assert.Equal("<x", filter.Push("x"));
        Assert.False(filter.Stopped);
    }
}

public class WorkerEngineDemuxTests
{
    [Fact]
    public async Task Multiplexed_events_demux_to_correct_jobs()
    {
        if (!ExLlamaV3WorkerEngine.TryResolvePython(out _))
        {
            // xunit v2 has no Assert.Skip; CI hosts without Python should not fail this suite.
            return;
        }

        var script = Path.Combine(AppContext.BaseDirectory, "Fixtures", "fake_exl3_worker.py");
        if (!File.Exists(script))
        {
            var repo = FindRepoRoot();
            Assert.False(string.IsNullOrEmpty(repo), "Could not locate fake_exl3_worker.py");
            script = Path.Combine(repo!, "tests", "ExLlamaSharp.Tests", "Fixtures", "fake_exl3_worker.py");
        }

        Assert.True(File.Exists(script), script);

        var modelDir = Path.Combine(Path.GetTempPath(), "exl3-fake-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(modelDir);
        try
        {
            await using var engine = new ExLlamaV3WorkerEngine(
                options: new WorkerEngineOptions
                {
                    WorkerScript = script,
                    MaxNumSeqs = 8,
                    MaxBatchedTokens = 1024,
                });

            await engine.LoadAsync(modelDir);
            engine.Start();

            var jobs = Enumerable.Range(0, 3).Select(i => engine.SubmitStreamAsync(new CompletionRequest
            {
                Prompt = $"job-{i}",
                MaxNewTokens = 8,
                Priority = i,
                JobId = Guid.NewGuid(),
            })).ToArray();

            var collected = await Task.WhenAll(jobs.Select(ConsumeAsync));

            Assert.Equal(3, collected.Length);
            Assert.All(collected, text =>
            {
                Assert.Contains("t3", text);
                Assert.Contains("t2", text);
                Assert.Contains("t1", text);
            });
        }
        finally
        {
            try
            {
                Directory.Delete(modelDir, recursive: true);
            }
            catch
            {
                // temp cleanup
            }
        }
    }

    private static async Task<string> ConsumeAsync(IAsyncEnumerable<CompletionDelta> stream)
    {
        var sb = new System.Text.StringBuilder();
        await foreach (var delta in stream)
        {
            sb.Append(delta.Text);
            if (delta.Eos)
            {
                break;
            }
        }

        return sb.ToString();
    }

    private static string? FindRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        for (var i = 0; i < 8 && dir is not null; i++)
        {
            var marker = Path.Combine(dir.FullName, "tests", "ExLlamaSharp.Tests", "Fixtures", "fake_exl3_worker.py");
            if (File.Exists(marker))
            {
                return dir.FullName;
            }

            dir = dir.Parent;
        }

        return null;
    }
}
