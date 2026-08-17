using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using ExLlamaSharp.Chat;
using ExLlamaSharp.Tokenizer;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace ExLlamaSharp.Engine;

/// <summary>
/// Real EXL3 inference via a Python worker that loads local <c>third_party/exllamav3</c>
/// (official CUDA kernels). Not a mock.
/// </summary>
public sealed class ExLlamaV3WorkerEngine : IInferenceEngine
{
    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly ILogger _logger;
    private readonly SimpleTokenizer _fallbackTokenizer = new();
    private readonly object _gate = new();
    private readonly Dictionary<Guid, CancellationTokenSource> _jobs = new();

    private Process? _process;
    private StreamWriter? _stdin;
    private StreamReader? _stdout;
    private int _nextId = 1;
    private bool _loaded;
    private bool _running;
    private bool _disposed;
    private string? _modelPath;
    private long _promptTokens;
    private long _generatedTokens;
    private long _finished;
    private double _lastTps;

    public ExLlamaV3WorkerEngine(ILogger? logger = null)
    {
        _logger = logger ?? NullLogger.Instance;
    }

    public bool IsMock => false;
    public bool IsLoaded => _loaded;
    public bool IsRunning => _running;

    /// <summary>True when a suitable Python + worker script can be located.</summary>
    public static bool IsAvailable(string? repoRoot = null)
    {
        try
        {
            return TryResolvePython(out _) && TryResolveWorkerScript(repoRoot, out _);
        }
        catch
        {
            return false;
        }
    }

    public static bool LooksLikeExl3Directory(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || path.StartsWith("mock://", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!Directory.Exists(path))
        {
            return false;
        }

        var configPath = Path.Combine(path, "config.json");
        var hasSafetensors = Directory.EnumerateFiles(path, "*.safetensors").Any();
        var hasTokenizer = File.Exists(Path.Combine(path, "tokenizer.json"));

        if (File.Exists(configPath))
        {
            try
            {
                var json = File.ReadAllText(configPath);
                if (json.Contains("exl3", StringComparison.OrdinalIgnoreCase) ||
                    json.Contains("\"quant_method\"", StringComparison.OrdinalIgnoreCase) &&
                    json.Contains("exl3", StringComparison.OrdinalIgnoreCase))
                {
                    return hasSafetensors || hasTokenizer;
                }

                // Some EXL3 packs still set quant_method nested under quantization_config
                using var doc = JsonDocument.Parse(json);
                if (ContainsExl3(doc.RootElement))
                {
                    return true;
                }
            }
            catch
            {
                // fall through
            }
        }

        return hasSafetensors && hasTokenizer && File.Exists(configPath);
    }

    private static bool ContainsExl3(JsonElement el)
    {
        switch (el.ValueKind)
        {
            case JsonValueKind.Object:
                foreach (var p in el.EnumerateObject())
                {
                    if (p.Name.Contains("quant", StringComparison.OrdinalIgnoreCase) &&
                        p.Value.ValueKind == JsonValueKind.String &&
                        p.Value.GetString()?.Contains("exl3", StringComparison.OrdinalIgnoreCase) == true)
                    {
                        return true;
                    }

                    if (ContainsExl3(p.Value))
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var i in el.EnumerateArray())
                {
                    if (ContainsExl3(i))
                    {
                        return true;
                    }
                }

                break;
            case JsonValueKind.String:
                return el.GetString()?.Contains("exl3", StringComparison.OrdinalIgnoreCase) == true;
        }

        return false;
    }

    public async Task LoadAsync(string modelPath, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(modelPath);

        await EnsureWorkerAsync(cancellationToken).ConfigureAwait(false);

        var maxTokens = 8192;
        var resp = await SendAsync(new
        {
            cmd = "load",
            path = Path.GetFullPath(modelPath),
            max_num_tokens = maxTokens,
        }, cancellationToken).ConfigureAwait(false);

        if (!resp.GetProperty("ok").GetBoolean())
        {
            var err = resp.TryGetProperty("error", out var e) ? e.GetString() : "load failed";
            throw new InvalidOperationException(err);
        }

        lock (_gate)
        {
            _modelPath = modelPath;
            _loaded = true;
        }

        _logger.LogInformation("ExLlamaV3WorkerEngine loaded {Path}", modelPath);
    }

    public async Task UnloadAsync(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_process is null || _process.HasExited)
        {
            _loaded = false;
            _modelPath = null;
            return;
        }

        try
        {
            await SendAsync(new { cmd = "unload" }, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Worker unload failed");
        }

        _loaded = false;
        _modelPath = null;
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (!_loaded)
        {
            throw new InvalidOperationException("Load a model before Start().");
        }

        _running = true;
    }

    public void Stop()
    {
        _running = false;
    }

    public async Task<CompletionResult> SubmitAsync(
        CompletionRequest request,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(request);

        if (!_loaded)
        {
            throw new InvalidOperationException("Model is not loaded.");
        }

        if (!_running)
        {
            throw new InvalidOperationException("Engine is not running. Call Start() first.");
        }

        var jobId = request.JobId ?? Guid.NewGuid();
        var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        lock (_gate)
        {
            _jobs[jobId] = cts;
        }

        var started = DateTime.UtcNow;
        try
        {
            var stops = BuildStopList(request);
            object payload = request.Messages is { Count: > 0 }
                ? new
                {
                    cmd = "chat",
                    messages = request.Messages.Select(m => new
                    {
                        role = RoleWire(m.Role),
                        content = m.Content ?? "",
                    }),
                    max_new_tokens = request.MaxNewTokens,
                    temperature = request.Temperature,
                    top_p = request.TopP,
                    top_k = request.TopK,
                    stop = stops,
                }
                : new
                {
                    cmd = "generate",
                    prompt = request.Prompt,
                    max_new_tokens = request.MaxNewTokens,
                    temperature = request.Temperature,
                    top_p = request.TopP,
                    top_k = request.TopK,
                    stop = stops,
                };

            var resp = await SendAsync(payload, cts.Token).ConfigureAwait(false);

            if (!resp.GetProperty("ok").GetBoolean())
            {
                var err = resp.TryGetProperty("error", out var e) ? e.GetString() : "generate failed";
                return new CompletionResult
                {
                    JobId = jobId,
                    Text = string.Empty,
                    TokenIds = [],
                    Failed = true,
                    Error = err,
                    Duration = DateTime.UtcNow - started,
                };
            }

            var text = Chat.ChatTemplate.StripSpecialTokens(
                resp.TryGetProperty("text", out var t) ? t.GetString() ?? "" : "");
            var promptTokens = resp.TryGetProperty("prompt_tokens", out var pt) ? pt.GetInt32() : 0;
            var completionTokens = resp.TryGetProperty("completion_tokens", out var ct) ? ct.GetInt32() : 0;
            var tokenIds = ReadIntArray(resp, "token_ids");
            if (resp.TryGetProperty("tokens_per_second", out var tps))
            {
                _lastTps = tps.GetDouble();
            }

            Interlocked.Add(ref _promptTokens, promptTokens);
            Interlocked.Add(ref _generatedTokens, completionTokens);
            Interlocked.Increment(ref _finished);

            return new CompletionResult
            {
                JobId = jobId,
                Text = text,
                TokenIds = tokenIds,
                PromptTokens = promptTokens,
                CompletionTokens = completionTokens,
                Duration = DateTime.UtcNow - started,
            };
        }
        catch (OperationCanceledException) when (cts.IsCancellationRequested)
        {
            return new CompletionResult
            {
                JobId = jobId,
                Text = string.Empty,
                TokenIds = [],
                Cancelled = true,
                Duration = DateTime.UtcNow - started,
            };
        }
        finally
        {
            lock (_gate)
            {
                _jobs.Remove(jobId);
            }

            cts.Dispose();
        }
    }

    public bool Cancel(Guid jobId)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(jobId, out var cts))
            {
                cts.Cancel();
                return true;
            }
        }

        return false;
    }

    public EngineMetrics GetMetrics()
    {
        int waiting;
        lock (_gate)
        {
            waiting = _jobs.Count;
        }

        return new EngineMetrics
        {
            TotalPromptTokens = Interlocked.Read(ref _promptTokens),
            TotalGeneratedTokens = Interlocked.Read(ref _generatedTokens),
            NumJobsWaiting = waiting,
            NumJobsRunning = waiting > 0 ? 1 : 0,
            NumJobsFinished = Interlocked.Read(ref _finished),
            TokensPerSecond = _lastTps,
            IsMock = false,
        };
    }

    public int[] Tokenize(string text)
    {
        if (!_loaded || _process is null || _process.HasExited)
        {
            return _fallbackTokenizer.Encode(text);
        }

        try
        {
            var resp = SendAsync(new { cmd = "tokenize", text }, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (resp.GetProperty("ok").GetBoolean())
            {
                return ReadIntArray(resp, "tokens");
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Worker tokenize failed; using fallback");
        }

        return _fallbackTokenizer.Encode(text);
    }

    public string Detokenize(ReadOnlySpan<int> tokens)
    {
        if (!_loaded || _process is null || _process.HasExited)
        {
            return _fallbackTokenizer.Decode(tokens);
        }

        try
        {
            var arr = tokens.ToArray();
            var resp = SendAsync(new { cmd = "detokenize", tokens = arr }, CancellationToken.None)
                .GetAwaiter()
                .GetResult();
            if (resp.GetProperty("ok").GetBoolean() && resp.TryGetProperty("text", out var t))
            {
                return t.GetString() ?? "";
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Worker detokenize failed; using fallback");
        }

        return _fallbackTokenizer.Decode(tokens);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Stop();
        KillWorker();
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private async Task EnsureWorkerAsync(CancellationToken cancellationToken)
    {
        if (_process is not null && !_process.HasExited)
        {
            return;
        }

        if (!TryResolvePython(out var python))
        {
            throw new InvalidOperationException(
                "Python not found. Run packaging/Setup-Exl3Python.ps1 or set EXLLAMASHARP_PYTHON.");
        }

        if (!TryResolveWorkerScript(null, out var script))
        {
            throw new InvalidOperationException("tools/exl3_worker/worker.py not found.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = $"\"{script}\"",
            WorkingDirectory = FindRepoRoot() ?? Environment.CurrentDirectory,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            // No BOM: Python json.loads rejects UTF-8 BOM on stdin lines.
            StandardOutputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            StandardInputEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
        };
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        // Windows: DeepSeek DSA Triton path is unavailable; Llama EXL3 does not need it.
        psi.Environment["EXL3_BC_DSA"] = "0";
        PrependNativeSearchPath(psi, python);
        TryAddDonorExtPath(psi, python);

        _logger.LogInformation("Starting EXL3 worker: {Python} {Script}", python, script);
        var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
        if (!proc.Start())
        {
            throw new InvalidOperationException("Failed to start EXL3 Python worker.");
        }

        _process = proc;
        _stdin = proc.StandardInput;
        _stdout = proc.StandardOutput;

        _ = Task.Run(() => DrainStderr(proc), CancellationToken.None);

        // Read ready line
        var readyLine = await _stdout.ReadLineAsync(cancellationToken).ConfigureAwait(false);
        if (string.IsNullOrWhiteSpace(readyLine))
        {
            KillWorker();
            throw new InvalidOperationException("EXL3 worker produced no ready handshake.");
        }

        using var readyDoc = JsonDocument.Parse(readyLine);
        if (!readyDoc.RootElement.TryGetProperty("ok", out var ok) || !ok.GetBoolean())
        {
            KillWorker();
            throw new InvalidOperationException($"EXL3 worker handshake failed: {readyLine}");
        }
    }

    private void DrainStderr(Process proc)
    {
        try
        {
            while (!proc.HasExited)
            {
                var line = proc.StandardError.ReadLine();
                if (line is null)
                {
                    break;
                }

                _logger.LogInformation("[exl3_worker] {Line}", line);
            }
        }
        catch
        {
            // process exited
        }
    }

    private async Task<JsonElement> SendAsync(object payload, CancellationToken cancellationToken)
    {
        await EnsureWorkerAsync(cancellationToken).ConfigureAwait(false);

        var id = Interlocked.Increment(ref _nextId);
        var node = JsonSerializer.SerializeToNode(payload, JsonOpts)!.AsObject();
        node["id"] = id;
        var line = node.ToJsonString();

        Process proc;
        StreamWriter stdin;
        StreamReader stdout;
        lock (_gate)
        {
            if (_process is null || _stdin is null || _stdout is null || _process.HasExited)
            {
                throw new InvalidOperationException("EXL3 worker is not running.");
            }

            proc = _process;
            stdin = _stdin;
            stdout = _stdout;
        }

        await stdin.WriteLineAsync(line.AsMemory(), cancellationToken).ConfigureAwait(false);
        await stdin.FlushAsync(cancellationToken).ConfigureAwait(false);

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (proc.HasExited)
            {
                throw new InvalidOperationException($"EXL3 worker exited with code {proc.ExitCode}.");
            }

            var respLine = await stdout.ReadLineAsync(cancellationToken).ConfigureAwait(false);
            if (respLine is null)
            {
                throw new InvalidOperationException("EXL3 worker closed stdout.");
            }

            using var doc = JsonDocument.Parse(respLine);
            var root = doc.RootElement.Clone();
            if (root.TryGetProperty("id", out var rid))
            {
                if (rid.ValueKind == JsonValueKind.Number && rid.GetInt32() == id)
                {
                    return root;
                }

                // unrelated / ready echoes — keep waiting
                continue;
            }

            // Responses without id (shouldn't happen after handshake) — accept if ok/error present
            if (root.TryGetProperty("ok", out _))
            {
                return root;
            }
        }
    }

    private void KillWorker()
    {
        try
        {
            _stdin?.Dispose();
        }
        catch
        {
            // ignore
        }

        _stdin = null;
        _stdout = null;

        if (_process is null)
        {
            return;
        }

        try
        {
            if (!_process.HasExited)
            {
                _process.Kill(entireProcessTree: true);
                _process.WaitForExit(3000);
            }
        }
        catch
        {
            // ignore
        }
        finally
        {
            _process.Dispose();
            _process = null;
        }
    }

    private static int[] ReadIntArray(JsonElement root, string name)
    {
        if (!root.TryGetProperty(name, out var arr) || arr.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        var list = new List<int>();
        foreach (var el in arr.EnumerateArray())
        {
            if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var v))
            {
                list.Add(v);
            }
        }

        return list.ToArray();
    }

    /// <summary>
    /// Windows Service (LocalSystem) has a minimal PATH. Prepend venv Scripts
    /// (ninja) and torch/lib (cublas/cudart) so <c>exllamav3_ext.pyd</c> can load.
    /// </summary>
    private static void PrependNativeSearchPath(ProcessStartInfo psi, string python)
    {
        var extras = new List<string>();
        try
        {
            var scripts = Path.GetDirectoryName(python);
            if (!string.IsNullOrWhiteSpace(scripts) && Directory.Exists(scripts))
            {
                extras.Add(scripts);
                var venv = Path.GetDirectoryName(scripts);
                if (!string.IsNullOrWhiteSpace(venv))
                {
                    var torchLib = Path.Combine(venv, "Lib", "site-packages", "torch", "lib");
                    if (Directory.Exists(torchLib))
                    {
                        extras.Add(torchLib);
                    }
                }
            }

            var cuda = Environment.GetEnvironmentVariable("CUDA_PATH");
            if (!string.IsNullOrWhiteSpace(cuda))
            {
                var cudaBin = Path.Combine(cuda, "bin");
                if (Directory.Exists(cudaBin))
                {
                    extras.Add(cudaBin);
                }
            }
        }
        catch
        {
            // best-effort
        }

        if (extras.Count == 0)
        {
            return;
        }

        var current = "";
        if (psi.Environment.TryGetValue("Path", out var existing) && !string.IsNullOrWhiteSpace(existing))
        {
            current = existing;
        }
        else
        {
            current = Environment.GetEnvironmentVariable("Path") ?? "";
        }

        psi.Environment["Path"] = string.Join(";", extras.Concat(new[] { current }).Where(s => !string.IsNullOrWhiteSpace(s)));
    }

    /// <summary>
    /// If the product venv has the PyPI source package (no <c>exllamav3_ext.pyd</c>),
    /// put a donor site-packages (repo <c>.venv-exl3</c>) on PYTHONPATH so the
    /// prebuilt CUDA extension can be imported.
    /// </summary>
    private static void TryAddDonorExtPath(ProcessStartInfo psi, string python)
    {
        try
        {
            var scripts = Path.GetDirectoryName(python);
            var venv = scripts is null ? null : Path.GetDirectoryName(scripts);
            if (string.IsNullOrWhiteSpace(venv))
            {
                return;
            }

            var localPyd = Path.Combine(venv, "Lib", "site-packages", "exllamav3_ext.cp312-win_amd64.pyd");
            if (File.Exists(localPyd))
            {
                return;
            }

            var donors = new List<string>();
            var repo = FindRepoRoot();
            if (repo is not null)
            {
                donors.Add(Path.Combine(repo, ".venv-exl3", "Lib", "site-packages"));
            }

            var programDataDonor = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ExLlamaSharp", "exl3-ext-donor.txt");
            if (File.Exists(programDataDonor))
            {
                var line = File.ReadAllText(programDataDonor).Trim();
                if (!string.IsNullOrWhiteSpace(line))
                {
                    donors.Add(line);
                }
            }

            foreach (var site in donors.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var donorPyd = Path.Combine(site, "exllamav3_ext.cp312-win_amd64.pyd");
                if (!File.Exists(donorPyd))
                {
                    continue;
                }

                psi.Environment.TryGetValue("PYTHONPATH", out var existing);
                psi.Environment["PYTHONPATH"] = string.IsNullOrWhiteSpace(existing)
                    ? site
                    : site + Path.PathSeparator + existing;
                return;
            }
        }
        catch
        {
            // best-effort
        }
    }

    public static bool TryResolvePython(out string python)
    {
        python = "";
        var env = Environment.GetEnvironmentVariable("EXLLAMASHARP_PYTHON");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            python = env;
            return true;
        }

        foreach (var cfg in EnumerateRuntimeConfigPaths())
        {
            if (TryReadPythonFromRuntimeConfig(cfg, out python))
            {
                return true;
            }
        }

        var candidates = new List<string>();
        foreach (var dir in EnumerateAppDirs())
        {
            candidates.Add(Path.Combine(dir, "venv", "Scripts", "python.exe"));
        }

        var root = FindRepoRoot();
        if (root is not null)
        {
            candidates.Add(Path.Combine(root, ".venv-exl3", "Scripts", "python.exe"));
        }

        // Legacy location (older installs)
        candidates.Add(Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ExLlamaSharp", "venv", "Scripts", "python.exe"));

        foreach (var c in candidates.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (File.Exists(c))
            {
                python = c;
                return true;
            }
        }

        // py -3 / python on PATH
        foreach (var name in new[] { "py", "python" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = name,
                    Arguments = name == "py" ? "-3 -c \"import sys; print(sys.executable)\"" : "-c \"import sys; print(sys.executable)\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                };
                using var p = Process.Start(psi);
                if (p is null)
                {
                    continue;
                }

                var output = p.StandardOutput.ReadToEnd().Trim();
                p.WaitForExit(5000);
                if (p.ExitCode == 0 && File.Exists(output))
                {
                    python = output;
                    return true;
                }
            }
            catch
            {
                // try next
            }
        }

        return false;
    }

    internal static bool TryResolveWorkerScript(string? repoRoot, out string script)
    {
        script = "";
        var root = repoRoot ?? FindRepoRoot();
        if (root is not null)
        {
            var path = Path.Combine(root, "tools", "exl3_worker", "worker.py");
            if (File.Exists(path))
            {
                script = path;
                return true;
            }
        }

        var beside = Path.Combine(AppContext.BaseDirectory, "tools", "exl3_worker", "worker.py");
        if (File.Exists(beside))
        {
            script = beside;
            return true;
        }

        return false;
    }

    private static IEnumerable<string> EnumerateAppDirs()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Add(string? d)
        {
            if (string.IsNullOrWhiteSpace(d))
            {
                return;
            }

            try
            {
                var full = Path.GetFullPath(d);
                if (seen.Add(full))
                {
                    // yield via list below
                }
            }
            catch
            {
                // ignore
            }
        }

        Add(AppContext.BaseDirectory);
        try
        {
            var proc = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(proc))
            {
                Add(Path.GetDirectoryName(proc));
            }
        }
        catch
        {
            // ignore
        }

        return seen;
    }

    private static IEnumerable<string> EnumerateRuntimeConfigPaths()
    {
        foreach (var dir in EnumerateAppDirs())
        {
            yield return Path.Combine(dir, "exl3-runtime.json");
        }

        yield return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ExLlamaSharp", "exl3-runtime.json");
    }

    private static bool TryReadPythonFromRuntimeConfig(string cfgPath, out string python)
    {
        python = "";
        try
        {
            if (!File.Exists(cfgPath))
            {
                return false;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(cfgPath));
            if (doc.RootElement.TryGetProperty("python", out var p) &&
                p.ValueKind == JsonValueKind.String)
            {
                var path = p.GetString();
                if (!string.IsNullOrWhiteSpace(path) && File.Exists(path))
                {
                    python = path;
                    return true;
                }
            }
        }
        catch
        {
            // ignore
        }

        return false;
    }

    private static string? FindRepoRoot()
    {
        // Walk up from BaseDirectory and cwd looking for tools/exl3_worker or third_party/exllamav3
        foreach (var start in new[] { AppContext.BaseDirectory, Environment.CurrentDirectory })
        {
            try
            {
                var dir = new DirectoryInfo(start);
                for (var i = 0; i < 8 && dir is not null; i++)
                {
                    var marker = Path.Combine(dir.FullName, "tools", "exl3_worker", "worker.py");
                    var exl3 = Path.Combine(dir.FullName, "third_party", "exllamav3");
                    if (File.Exists(marker) || Directory.Exists(exl3))
                    {
                        return dir.FullName;
                    }

                    dir = dir.Parent;
                }
            }
            catch
            {
                // ignore
            }
        }

        return null;
    }

    private static string RoleWire(ChatRole role) => role switch
    {
        ChatRole.System => "system",
        ChatRole.User => "user",
        ChatRole.Assistant => "assistant",
        ChatRole.Tool => "tool",
        _ => "user",
    };

    private static List<object> BuildStopList(CompletionRequest request)
    {
        var stops = new List<object>();
        if (request.StopTokenId >= 0)
        {
            stops.Add(request.StopTokenId);
        }

        foreach (var s in ChatTemplate.DefaultStopStrings)
        {
            stops.Add(s);
        }

        if (request.StopStrings is { Count: > 0 })
        {
            foreach (var s in request.StopStrings)
            {
                if (!string.IsNullOrEmpty(s))
                {
                    stops.Add(s);
                }
            }
        }

        return stops;
    }
}
