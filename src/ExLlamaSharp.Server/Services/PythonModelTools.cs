using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ExLlamaSharp.Server.Services;

/// <summary>
/// Spawns optional Python helpers for convert / pull workflows.
/// </summary>
public sealed class PythonModelTools
{
    private readonly ILogger<PythonModelTools> _logger;

    public PythonModelTools(ILogger<PythonModelTools> logger)
    {
        _logger = logger;
    }

    public string? PythonExecutable { get; set; }

    public string ResolvePython()
    {
        if (!string.IsNullOrWhiteSpace(PythonExecutable) && File.Exists(PythonExecutable))
        {
            return PythonExecutable;
        }

        if (ExLlamaSharp.Engine.ExLlamaV3WorkerEngine.TryResolvePython(out var resolved) && File.Exists(resolved))
        {
            return resolved;
        }

        foreach (var candidate in new[] { "python", "python3", "py" })
        {
            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = candidate,
                    Arguments = candidate == "py" ? "-3 --version" : "--version",
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

                p.WaitForExit(3000);
                if (p.ExitCode == 0)
                {
                    return candidate;
                }
            }
            catch
            {
                // try next
            }
        }

        throw new InvalidOperationException(
            "No Python interpreter found. Run Setup-Exl3Python from the Start Menu, or set EXLLAMASHARP_PYTHON.");
    }

    public ProcessStartInfo CreateConvertStartInfo(
        string inputPath,
        string outputPath,
        string workPath,
        double bits = 4.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(workPath);

        var python = ResolvePython();
        var bitsLit = bits.ToString(System.Globalization.CultureInfo.InvariantCulture);
        var args =
            $"-m exllamav3.conversion.convert_model -i \"{inputPath}\" -o \"{outputPath}\" -w \"{workPath}\" -b {bitsLit}";

        var convertPy = FindConvertPy(python);
        if (convertPy is not null)
        {
            args = $"\"{convertPy}\" -i \"{inputPath}\" -o \"{outputPath}\" -w \"{workPath}\" -b {bitsLit}";
        }

        return new ProcessStartInfo
        {
            FileName = python,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
    }

    private static string? FindConvertPy(string pythonExe)
    {
        try
        {
            var dir = Path.GetDirectoryName(pythonExe);
            if (string.IsNullOrEmpty(dir))
            {
                return null;
            }

            foreach (var candidate in new[]
                     {
                         Path.Combine(dir, "convert.py"),
                         Path.GetFullPath(Path.Combine(dir, "..", "Scripts", "convert.py")),
                     })
            {
                if (File.Exists(candidate))
                {
                    return candidate;
                }
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    public ProcessStartInfo CreateConvertStartInfo(
        string inputPath,
        string outputPath,
        QuantizationMode mode = QuantizationMode.EXL3,
        double bits = 4.0)
    {
        var work = Path.Combine(Path.GetTempPath(), "exllamasharp-quantize-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(work);
        _ = mode;
        return CreateConvertStartInfo(inputPath, outputPath, work, bits);
    }

    public ProcessStartInfo CreatePullStartInfo(string repoId, string destinationDir, string? revision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDir);

        var python = ResolvePython();
        var rev = string.IsNullOrWhiteSpace(revision) ? "main" : revision;
        var scriptPath = Path.Combine(Path.GetTempPath(), "exllamasharp-hf-pull.py");
        File.WriteAllText(scriptPath, """
import os, subprocess, sys
try:
    from huggingface_hub import snapshot_download
except ImportError:
    subprocess.check_call([sys.executable, "-m", "pip", "install", "-q", "huggingface_hub>=0.23"])
    from huggingface_hub import snapshot_download
repo, dest, rev = sys.argv[1], sys.argv[2], sys.argv[3]
tok = os.environ.get("HF_TOKEN") or os.environ.get("HUGGING_FACE_HUB_TOKEN")
print(snapshot_download(repo_id=repo, local_dir=dest, revision=rev, token=tok or None), flush=True)
""");
        var args = $"\"{scriptPath}\" \"{repoId}\" \"{destinationDir}\" \"{rev}\"";

        var psi = new ProcessStartInfo
        {
            FileName = python,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.Environment["PYTHONUNBUFFERED"] = "1";
        return psi;
    }

    /// <summary>
    /// Starts the process. Caller owns disposal. Returns null if the OS cannot start the process.
    /// </summary>
    public Process? Start(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        _logger.LogInformation("Python spawn: {File} {Args}", startInfo.FileName, startInfo.Arguments);
        try
        {
            return Process.Start(startInfo);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to start Python tool process");
            return null;
        }
    }

    public async Task<(int ExitCode, string StdOut, string StdErr)> RunAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Could not start Python process.");

        await using var killReg = cancellationToken.Register(() =>
        {
            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch
            {
                // already exited
            }
        });

        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
        var stdout = await stdoutTask.ConfigureAwait(false);
        var stderr = await stderrTask.ConfigureAwait(false);
        return (process.ExitCode, stdout, stderr);
    }
}
