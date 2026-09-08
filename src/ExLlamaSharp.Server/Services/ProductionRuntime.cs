using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace ExLlamaSharp.Server.Services;

/// <summary>Shared on-disk signals between Server and Tray (listen URL, restart, host mode, data ACL).</summary>
public static class ProductionRuntime
{
    public const string RestartFileName = "restart.request";
    public const string FirewallFileName = "firewall.request";
    public const string ListenFileName = "listen.json";
    public const string HostModeFileName = "host-mode.json";
    public const string ServerMutexName = @"Global\ExLlamaSharp.Server";
    public const string FirewallRuleNamePrefix = "ExLlamaSharp HTTP ";

    public static string DataRoot
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("EXLLAMASHARP_DATA_ROOT");
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env;
            }

            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ExLlamaSharp");
        }
    }

    public static string RestartRequestPath => Path.Combine(DataRoot, RestartFileName);

    public static string FirewallRequestPath => Path.Combine(DataRoot, FirewallFileName);

    public static string ListenFilePath => Path.Combine(DataRoot, ListenFileName);

    public static string HostModeFilePath => Path.Combine(DataRoot, HostModeFileName);

    public static string LogsDirectory => Path.Combine(DataRoot, "logs");

    /// <summary>True when this process is in Windows Session 0 (services / LocalSystem).</summary>
    public static bool IsSessionZero()
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            using var p = Process.GetCurrentProcess();
            return p.SessionId == 0;
        }
        catch
        {
            return false;
        }
    }

    public static string ReadHostMode()
    {
        try
        {
            if (File.Exists(HostModeFilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(HostModeFilePath));
                if (doc.RootElement.TryGetProperty("mode", out var mode))
                {
                    var value = mode.GetString();
                    if (string.Equals(value, "headless", StringComparison.OrdinalIgnoreCase))
                    {
                        return "headless";
                    }
                }
            }
        }
        catch
        {
            // default desktop
        }

        return "desktop";
    }

    public static void WriteHostMode(string mode)
    {
        Directory.CreateDirectory(DataRoot);
        var normalized = string.Equals(mode, "headless", StringComparison.OrdinalIgnoreCase)
            ? "headless"
            : "desktop";
        File.WriteAllText(HostModeFilePath, JsonSerializer.Serialize(new
        {
            mode = normalized,
            written_utc = DateTime.UtcNow.ToString("o"),
        }));
    }

    public static bool IsHeadless => string.Equals(ReadHostMode(), "headless", StringComparison.OrdinalIgnoreCase);

    public static void EnsureWritableDataRoot()
    {
        Directory.CreateDirectory(DataRoot);
        Directory.CreateDirectory(LogsDirectory);
        Directory.CreateDirectory(Path.Combine(DataRoot, "models"));
        Directory.CreateDirectory(Path.Combine(DataRoot, "backups"));
        Directory.CreateDirectory(Path.Combine(DataRoot, "dp-keys"));

        ApplyUsersModifyAcl(DataRoot, recursive: true);
        foreach (var dbFile in Directory.EnumerateFiles(DataRoot, "app.db*"))
        {
            ApplyUsersModifyAcl(dbFile, recursive: false);
        }

        var probe = Path.Combine(DataRoot, ".write-probe");
        try
        {
            File.WriteAllText(probe, DateTime.UtcNow.ToString("o"));
            File.Delete(probe);
        }
        catch (Exception ex)
        {
            throw new InvalidOperationException(
                $"Data root is not writable: {DataRoot}. " +
                "Grant Modify to Users on %ProgramData%\\ExLlamaSharp (and app.db). " +
                ex.Message,
                ex);
        }
    }

    public static void ApplyUsersModifyAcl(string path, bool recursive)
    {
        try
        {
            if (File.Exists(path))
            {
                var file = new FileInfo(path);
                var acl = file.GetAccessControl();
                var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                acl.AddAccessRule(new FileSystemAccessRule(
                    users,
                    FileSystemRights.Modify,
                    AccessControlType.Allow));
                file.SetAccessControl(acl);
                return;
            }

            if (Directory.Exists(path))
            {
                var dir = new DirectoryInfo(path);
                var acl = dir.GetAccessControl();
                var users = new SecurityIdentifier(WellKnownSidType.BuiltinUsersSid, null);
                acl.AddAccessRule(new FileSystemAccessRule(
                    users,
                    FileSystemRights.Modify,
                    InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                    PropagationFlags.None,
                    AccessControlType.Allow));
                dir.SetAccessControl(acl);
            }
        }
        catch
        {
            try
            {
                var args = recursive
                    ? $"\"{path}\" /grant *S-1-5-32-545:(OI)(CI)M /T"
                    : $"\"{path}\" /grant *S-1-5-32-545:M";
                using var p = Process.Start(new ProcessStartInfo
                {
                    FileName = "icacls.exe",
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });
                p?.WaitForExit(8_000);
            }
            catch
            {
                // installer also sets this
            }
        }
    }

    public static void WriteListenFile(string bind, int port, bool tls)
    {
        var loopback = bind is "0.0.0.0" or "*" or ""
            ? "127.0.0.1"
            : bind is "localhost" ? "127.0.0.1" : bind;
        var scheme = tls ? "https" : "http";
        var payload = new
        {
            url = $"{scheme}://{loopback}:{port}",
            bind,
            port,
            tls,
            written_utc = DateTime.UtcNow.ToString("o"),
        };
        File.WriteAllText(ListenFilePath, JsonSerializer.Serialize(payload));
    }

    public static string? TryReadListenUrl()
    {
        try
        {
            if (!File.Exists(ListenFilePath))
            {
                return null;
            }

            using var doc = JsonDocument.Parse(File.ReadAllText(ListenFilePath));
            if (doc.RootElement.TryGetProperty("url", out var url))
            {
                return url.GetString()?.TrimEnd('/');
            }
        }
        catch
        {
            // ignore
        }

        return null;
    }

    public static void RequestRestart()
    {
        Directory.CreateDirectory(DataRoot);
        File.WriteAllText(RestartRequestPath, DateTime.UtcNow.ToString("o"));
    }

    public static void ClearRestartRequest()
    {
        try
        {
            if (File.Exists(RestartRequestPath))
            {
                File.Delete(RestartRequestPath);
            }
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// Ask the Tray (elevated) to add or remove the inbound Windows Firewall rule for the API port.
    /// </summary>
    public static void RequestFirewallRule(bool enable, int port)
    {
        if (port is < 1 or > 65535)
        {
            return;
        }

        Directory.CreateDirectory(DataRoot);
        File.WriteAllText(FirewallRequestPath, JsonSerializer.Serialize(new
        {
            enable,
            port,
            rule_name = FirewallRuleNamePrefix + port,
            written_utc = DateTime.UtcNow.ToString("o"),
        }));
    }

    public static bool IsLanBind(string? bind) =>
        bind is "0.0.0.0" or "*"
        || (!string.IsNullOrWhiteSpace(bind)
            && bind is not ("127.0.0.1" or "localhost" or "::1"));

    /// <summary>IPv4 addresses on up, non-loopback NICs (for Admin LAN URL hints).</summary>
    public static IReadOnlyList<string> GetLanIpv4Addresses()
    {
        var list = new List<string>();
        try
        {
            foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
            {
                if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up)
                {
                    continue;
                }

                if (ni.NetworkInterfaceType is System.Net.NetworkInformation.NetworkInterfaceType.Loopback)
                {
                    continue;
                }

                foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                {
                    if (ua.Address.AddressFamily != System.Net.Sockets.AddressFamily.InterNetwork)
                    {
                        continue;
                    }

                    if (System.Net.IPAddress.IsLoopback(ua.Address))
                    {
                        continue;
                    }

                    var s = ua.Address.ToString();
                    if (!list.Contains(s, StringComparer.Ordinal))
                    {
                        list.Add(s);
                    }
                }
            }
        }
        catch
        {
            // optional UI hint
        }

        return list;
    }
}
