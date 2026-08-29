using ExLlamaSharp.Chat;
using ExLlamaSharp.Engine;
using Microsoft.Extensions.Logging.Abstractions;

if (args.Length == 0 || args is ["--help"] or ["-h"])
{
    PrintHelp();
    return 0;
}

var command = args[0].ToLowerInvariant();
return command switch
{
    "version" => PrintVersion(),
    "chat" => await RunChatAsync(args.Skip(1).ToArray()),
    "bench" => await RunBenchAsync(args.Skip(1).ToArray()),
    "tokenize" => RunTokenize(args.Skip(1).ToArray()),
    _ => Unknown(command),
};

static void PrintHelp()
{
    Console.WriteLine("""
        ExLlamaSharp CLI

        Usage:
          exllamasharp version
          exllamasharp chat [--model <path>] [--mock] [--max-tokens N] <prompt>
          exllamasharp bench [--n N] [--mock]
          exllamasharp tokenize <text>

        Engine selection for chat:
          --mock              Force the mock engine
          --model <dir>       Prefer ExLlamaV3WorkerEngine when the path is an EXL3
                              directory and the Python worker runtime is available;
                              otherwise fall back to the native/mock engine.
        """);
}

static int PrintVersion()
{
    Console.WriteLine("ExLlamaSharp CLI 1.0.0 (.NET 10)");
    return 0;
}

static int Unknown(string command)
{
    Console.Error.WriteLine($"Unknown command: {command}");
    PrintHelp();
    return 1;
}

static async Task<int> RunChatAsync(string[] args)
{
    var forceMock = args.Contains("--mock");
    var model = GetOption(args, "--model") ?? "mock://llama3";
    var maxTokens = int.TryParse(GetOption(args, "--max-tokens"), out var mt) ? mt : 64;

    var positional = new List<string>();
    for (var i = 0; i < args.Length; i++)
    {
        if (args[i] is "--model" or "--max-tokens")
        {
            i++;
            continue;
        }

        if (args[i] == "--mock")
        {
            continue;
        }

        positional.Add(args[i]);
    }

    var userPrompt = positional.Count > 0 ? string.Join(' ', positional) : "Hello!";
    var messages = new List<ChatMessage>
    {
        new() { Role = ChatRole.System, Content = "You are a helpful assistant." },
        new() { Role = ChatRole.User, Content = userPrompt },
    };
    var prompt = ChatTemplate.Format(messages);

    await using var engine = CreateEngine(model, forceMock);
    await engine.LoadAsync(model);
    engine.Start();

    var request = new CompletionRequest
    {
        Prompt = prompt,
        MaxNewTokens = maxTokens,
        Temperature = 0.7f,
        TopP = 0.9f,
    };

    var result = await engine.SubmitAsync(request);
    Console.WriteLine(result.Text);
    return result.Failed ? 1 : 0;
}

static IInferenceEngine CreateEngine(string model, bool forceMock)
{
    if (forceMock || model.StartsWith("mock://", StringComparison.OrdinalIgnoreCase))
    {
        return ExLlamaEngine.Create(forceMock: true);
    }

    if (ExLlamaV3WorkerEngine.LooksLikeExl3Directory(model) && ExLlamaV3WorkerEngine.IsAvailable())
    {
        Console.Error.WriteLine("Using ExLlamaV3WorkerEngine (EXL3 Python worker).");
        return new ExLlamaV3WorkerEngine(NullLogger.Instance);
    }

    return ExLlamaEngine.Create(forceMock: false);
}

static async Task<int> RunBenchAsync(string[] args)
{
    var n = int.TryParse(GetOption(args, "--n"), out var parsed) ? parsed : 8;
    var forceMock = args.Contains("--mock");

    await using var engine = ExLlamaEngine.Create(forceMock: forceMock || true);
    await engine.LoadAsync("mock://bench");
    engine.Start();

    var sw = System.Diagnostics.Stopwatch.StartNew();
    var tasks = Enumerable.Range(0, n).Select(async i =>
    {
        var req = new CompletionRequest
        {
            Prompt = $"Bench prompt {i}",
            MaxNewTokens = 32,
            Temperature = 0f,
        };
        return await engine.SubmitAsync(req);
    });
    var results = await Task.WhenAll(tasks);
    sw.Stop();

    var totalTokens = results.Sum(r => r.CompletionTokens);
    var tps = totalTokens / Math.Max(sw.Elapsed.TotalSeconds, 0.001);
    Console.WriteLine($"Jobs={n} tokens={totalTokens} elapsed={sw.Elapsed.TotalMilliseconds:F0}ms tok/s={tps:F1}");
    return 0;
}

static int RunTokenize(string[] args)
{
    var text = args.Length > 0 ? string.Join(' ', args) : "";
    var tokenizer = new ExLlamaSharp.Tokenizer.SimpleTokenizer();
    var ids = tokenizer.Encode(text);
    Console.WriteLine($"[{string.Join(", ", ids)}]");
    Console.WriteLine($"count={ids.Length}");
    return 0;
}

static string? GetOption(string[] args, string name)
{
    for (var i = 0; i < args.Length - 1; i++)
    {
        if (args[i] == name)
        {
            return args[i + 1];
        }
    }

    return null;
}
