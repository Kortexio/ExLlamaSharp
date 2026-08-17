using System.Diagnostics;
using System.Text.Json;

namespace ExLlamaSharp.Engine.Worker;

/// <summary>Resolves Python, worker.py, and native DLL search paths for the EXL3 worker process.</summary>
internal static class WorkerRuntimeLocator
{
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

        foreach (var name in new[] { "py", "python" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = name,
                    Arguments = name == "py"
                        ? "-3 -c \"import sys; print(sys.executable)\""
                        : "-c \"import sys; print(sys.executable)\"",
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

    public static bool TryResolveWorkerScript(string? repoRoot, out string script)
    {
        script = "";
        var env = Environment.GetEnvironmentVariable("EXLLAMASHARP_WORKER_SCRIPT");
        if (!string.IsNullOrWhiteSpace(env) && File.Exists(env))
        {
            script = env;
            return true;
        }

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

    public static string ResolvePython(WorkerEngineOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.PythonPath) && File.Exists(options.PythonPath))
        {
            return options.PythonPath;
        }

        if (TryResolvePython(out var python))
        {
            return python;
        }

        throw new InvalidOperationException(
            "Python not found. Run packaging/Setup-Exl3Python.ps1 or set EXLLAMASHARP_PYTHON.");
    }

    public static string ResolveWorkerScript(WorkerEngineOptions options)
    {
        if (!string.IsNullOrWhiteSpace(options.WorkerScript) && File.Exists(options.WorkerScript))
        {
            return options.WorkerScript;
        }

        if (TryResolveWorkerScript(null, out var script))
        {
            return script;
        }

        throw new InvalidOperationException("tools/exl3_worker/worker.py not found.");
    }

    public static void ConfigureProcessEnvironment(ProcessStartInfo psi, string python)
    {
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        psi.Environment["PYTHONIOENCODING"] = "utf-8";
        psi.Environment["EXL3_BC_DSA"] = "0";
        PrependNativeSearchPath(psi, python);
        TryAddDonorExtPath(psi, python);
    }

    public static string? FindRepoRoot()
    {
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

        var current = psi.Environment.TryGetValue("Path", out var existing) && !string.IsNullOrWhiteSpace(existing)
            ? existing
            : Environment.GetEnvironmentVariable("Path") ?? "";

        psi.Environment["Path"] = string.Join(";", extras.Concat(new[] { current }).Where(s => !string.IsNullOrWhiteSpace(s)));
    }

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
                seen.Add(Path.GetFullPath(d));
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
}
