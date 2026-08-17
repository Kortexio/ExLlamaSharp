using System.Diagnostics;
using Microsoft.Extensions.Logging;

namespace ExLlamaSharp.Server.Services;

/// <summary>
/// Spawns optional Python helpers for convert / pull workflows.
/// Stub: builds <see cref="ProcessStartInfo"/> and can start a process when Python is available.
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

        return "python";
    }

    public ProcessStartInfo CreateConvertStartInfo(
        string inputPath,
        string outputPath,
        QuantizationMode mode = QuantizationMode.EXL3,
        double bits = 4.0)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(inputPath);
        ArgumentException.ThrowIfNullOrWhiteSpace(outputPath);

        var python = ResolvePython();
        var args =
            $"-m exllamasharp_tools.convert --input \"{inputPath}\" --output \"{outputPath}\" " +
            $"--mode {QuantizationModes.ToWireName(mode)} --bits {bits.ToString(System.Globalization.CultureInfo.InvariantCulture)}";

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

    public ProcessStartInfo CreatePullStartInfo(string repoId, string destinationDir, string? revision = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repoId);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationDir);

        var python = ResolvePython();
        var rev = string.IsNullOrWhiteSpace(revision) ? "main" : revision;
        var scriptPath = Path.Combine(Path.GetTempPath(), "exllamasharp-hf-pull.py");
        File.WriteAllText(scriptPath, """
import os, sys
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
    /// Starts the process. Stub default: does not wait for completion; caller owns disposal.
    /// Returns null if the OS cannot start the process.
    /// </summary>
    public Process? StartStub(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);
        _logger.LogInformation("Python stub spawn: {File} {Args}", startInfo.FileName, startInfo.Arguments);
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

    public async Task<(int ExitCode, string StdOut, string StdErr)> RunStubAsync(
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
