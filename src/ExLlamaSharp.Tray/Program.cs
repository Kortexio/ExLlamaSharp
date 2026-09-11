using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.ServiceProcess;

namespace ExLlamaSharp.Tray;

internal static class TrayPaths
{
    public static string DataRoot =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("EXLLAMASHARP_DATA_ROOT"))
            ? Environment.GetEnvironmentVariable("EXLLAMASHARP_DATA_ROOT")!
            : Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
                "ExLlamaSharp");

    public static string AdminUrl
    {
        get
        {
            var env = Environment.GetEnvironmentVariable("EXLLAMASHARP_URL");
            if (!string.IsNullOrWhiteSpace(env))
            {
                return env.TrimEnd('/');
            }

            var listen = Path.Combine(DataRoot, "listen.json");
            try
            {
                if (File.Exists(listen))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(listen));
                    if (doc.RootElement.TryGetProperty("url", out var url)
                        && url.GetString() is { Length: > 0 } u)
                    {
                        return u.TrimEnd('/');
                    }
                }
            }
            catch
            {
                // default
            }

            return "http://127.0.0.1:14563";
        }
    }

    public static bool IsHeadless
    {
        get
        {
            var path = Path.Combine(DataRoot, "host-mode.json");
            try
            {
                if (File.Exists(path))
                {
                    using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(path));
                    if (doc.RootElement.TryGetProperty("mode", out var mode)
                        && string.Equals(mode.GetString(), "headless", StringComparison.OrdinalIgnoreCase))
                    {
                        return true;
                    }
                }
            }
            catch
            {
                // desktop
            }

            return false;
        }
    }
}

internal static class Program
{
    private const string MutexName = "Global\\ExLlamaSharp.Tray.SingleInstance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            // Second click: bring UI up via the already-running tray instance by opening the Admin URL.
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = TrayPaths.AdminUrl + "/",
                    UseShellExecute = true
                });
            }
            catch
            {
                // ignore
            }

            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
    private const string ServiceName = "ExLlamaSharp";
    private static readonly string InstallDir =
        Path.GetDirectoryName(Environment.ProcessPath) ?? AppContext.BaseDirectory;

    private readonly NotifyIcon _tray;
    private readonly System.Windows.Forms.Timer _timer;
    private readonly SynchronizationContext _ui;
    private readonly ToolStripMenuItem _statusItem;
    private readonly ToolStripMenuItem _startItem;
    private readonly ToolStripMenuItem _stopItem;
    private readonly ToolStripMenuItem _restartItem;
    private readonly Icon _iconOk;
    private readonly Icon _iconWarn;
    private readonly Icon _iconOff;
    private readonly Bitmap _bmpOk;
    private readonly Bitmap _bmpWarn;
    private readonly Bitmap _bmpOff;
    private bool _refreshing;
    private bool _starting;
    private bool _intentionalStop;
    private int _autoRecoverAttempts;
    private DateTime _lastAutoRecoverUtc = DateTime.MinValue;
    private DateTime _lastStableUtc = DateTime.MinValue;
    private FileSystemWatcher? _restartWatcher;
    private FileSystemWatcher? _firewallWatcher;
    private int _firewallBusy;

    public TrayApplicationContext()
    {
        try
        {
            _ui = SynchronizationContext.Current ?? new SynchronizationContext();
            WatchRestartRequest();
            WatchFirewallRequest();
            TryProcessFirewallRequest();

            _bmpOk = CreateIconBitmap(Color.FromArgb(34, 197, 94));
            _bmpWarn = CreateIconBitmap(Color.FromArgb(234, 179, 8));
            _bmpOff = CreateIconBitmap(Color.FromArgb(148, 163, 184));

            _iconOk = Icon.FromHandle(_bmpOk.GetHicon());
            _iconWarn = Icon.FromHandle(_bmpWarn.GetHicon());
            _iconOff = Icon.FromHandle(_bmpOff.GetHicon());

            _statusItem = new ToolStripMenuItem("Status: …") { Enabled = false };
            _startItem = new ToolStripMenuItem("Start server", null, (_, _) => _ = StartServerAsync(openUi: false));
            _stopItem = new ToolStripMenuItem("Stop server", null, (_, _) => _ = StopServerAsync());
            _restartItem = new ToolStripMenuItem("Restart server", null, (_, _) => _ = RestartServerAsync());

            var menu = new ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open Admin UI", null, (_, _) => _ = StartServerAsync(openUi: true));
            menu.Items.Add("Open data folder", null, (_, _) => OpenDataFolder());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add(_startItem);
            menu.Items.Add(_stopItem);
            menu.Items.Add(_restartItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Exit tray", null, (_, _) => ExitThread());

            _tray = new NotifyIcon
            {
                Icon = _iconOff,
                Text = "ExLlamaSharp",
                Visible = true,
                ContextMenuStrip = menu
            };
            _tray.DoubleClick += (_, _) => _ = StartServerAsync(openUi: true);

            Application.DoEvents();

            _timer = new System.Windows.Forms.Timer { Interval = 4000 };
            _timer.Tick += (_, _) =>
            {
                if (_refreshing)
                {
                    return;
                }

                _ = RefreshStatusAsync();
            };
            _timer.Start();

            // One-click UX: launching the tray starts the server if needed.
            _ = BootstrapAsync();
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Erro ao criar tray icon:\n{ex.Message}\n\n{ex.StackTrace}",
                "ExLlamaSharp Tray Error",
                MessageBoxButtons.OK,
                MessageBoxIcon.Error);
            throw;
        }
    }

    private async Task BootstrapAsync()
    {
        ShowBalloon("ExLlamaSharp", "A iniciar o servidor…");
        var ok = await EnsureServerRunningAsync().ConfigureAwait(false);
        await RefreshStatusAsync().ConfigureAwait(false);
        if (ok)
        {
            ShowBalloon("ExLlamaSharp", "Servidor pronto. Duplo-clique para abrir o Admin.");
        }
        else
        {
            ShowBalloon("ExLlamaSharp", "Não foi possível iniciar o servidor. Usa Start server no menu.");
        }
    }

    private static Bitmap CreateIconBitmap(Color accent)
    {
        const int size = 32;
        var bmp = new Bitmap(size, size);
        using (var g = Graphics.FromImage(bmp))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(Color.Transparent);
            using var bg = new SolidBrush(Color.FromArgb(30, 41, 59));
            g.FillEllipse(bg, 1, 1, size - 3, size - 3);
            using var ring = new Pen(accent, 3f);
            g.DrawEllipse(ring, 4, 4, size - 9, size - 9);
            using var core = new SolidBrush(accent);
            g.FillEllipse(core, 11, 11, 10, 10);
        }

        return bmp;
    }

    private static string AdminUrl => TrayPaths.AdminUrl;

    private static string ServerExe => Path.Combine(InstallDir, "ExLlamaSharp.Server.exe");

    private void ShowBalloon(string title, string text)
    {
        try
        {
            PostUi(() =>
            {
                _tray.BalloonTipTitle = title;
                _tray.BalloonTipText = text;
                _tray.BalloonTipIcon = ToolTipIcon.Info;
                _tray.ShowBalloonTip(4000);
            });
        }
        catch
        {
            // optional
        }
    }

    private static void EnsureUserAutostart()
    {
        try
        {
            var exe = Environment.ProcessPath;
            if (string.IsNullOrWhiteSpace(exe) || !File.Exists(exe))
            {
                return;
            }

            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(
                @"Software\Microsoft\Windows\CurrentVersion\Run");
            key?.SetValue("ExLlamaSharpTray", $"\"{exe}\"", Microsoft.Win32.RegistryValueKind.String);
        }
        catch
        {
            // optional
        }
    }

    private async Task StartServerAsync(bool openUi)
    {
        _intentionalStop = false;
        PostUi(() =>
        {
            _statusItem.Text = "Status: a iniciar…";
            _tray.Text = "ExLlamaSharp\nA iniciar…";
        });

        var ok = await EnsureServerRunningAsync().ConfigureAwait(false);
        await RefreshStatusAsync().ConfigureAwait(false);
        if (!ok)
        {
            var serverAlive = Process.GetProcessesByName("ExLlamaSharp.Server").Length > 0;
            PostUi(() => MessageBox.Show(
                serverAlive
                    ? "O servidor está a arrancar (provavelmente a carregar o modelo na GPU).\n\n" +
                      "Espera um minuto e usa Open Admin UI. O Admin fica disponível mesmo se o load falhar."
                    : "Não foi possível iniciar o servidor ExLlamaSharp.\n\n" +
                      "Tenta: menu do tray → Start server\nou reinicia o PC e volta a abrir o ícone.",
                "ExLlamaSharp",
                MessageBoxButtons.OK,
                serverAlive ? MessageBoxIcon.Information : MessageBoxIcon.Warning));
            if (serverAlive && openUi)
            {
                OpenUi();
            }

            return;
        }

        if (openUi)
        {
            OpenUi();
        }
    }

    private async Task StopServerAsync()
    {
        _intentionalStop = true;
        PostUi(() =>
        {
            _statusItem.Text = "Status: a parar…";
            _tray.Text = "ExLlamaSharp\nA parar…";
            _startItem.Enabled = false;
            _stopItem.Enabled = false;
            _restartItem.Enabled = false;
        });

        try
        {
            if (!TryStopService())
            {
                // Last resort: elevated sc.exe (one UAC prompt)
                RunElevatedSc("stop");
            }

            KillServerProcesses();
            await Task.Delay(800).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            PostUi(() => MessageBox.Show(ex.Message, "ExLlamaSharp", MessageBoxButtons.OK, MessageBoxIcon.Warning));
        }

        await RefreshStatusAsync().ConfigureAwait(false);
    }

    private async Task RestartServerAsync()
    {
        PostUi(() =>
        {
            _statusItem.Text = "Status: a reiniciar…";
            _tray.Text = "ExLlamaSharp\nA reiniciar…";
            ShowBalloon("ExLlamaSharp", "A reiniciar o servidor…");
        });

        await StopServerAsync().ConfigureAwait(false);
        _intentionalStop = false;
        await Task.Delay(1000).ConfigureAwait(false);
        var ok = await EnsureServerRunningAsync().ConfigureAwait(false);
        await RefreshStatusAsync().ConfigureAwait(false);
        ShowBalloon("ExLlamaSharp", ok ? "Servidor reiniciado." : "Falha ao reiniciar. Tenta Start server.");
    }

    private async Task<bool> EnsureServerRunningAsync()
    {
        if (_starting)
        {
            return await WaitForHealthAsync(TimeSpan.FromSeconds(20)).ConfigureAwait(false);
        }

        _starting = true;
        try
        {
            if (await IsHealthyAsync().ConfigureAwait(false))
            {
                // Prefer user-session process for CUDA. If only the LocalSystem service is up,
                // GPU model loads often hang with VRAM unused — migrate to App mode.
                if (!TrayPaths.IsHeadless && IsServiceRunning() && !IsUserSessionServerRunning())
                {
                    ShowBalloon("ExLlamaSharp", "A mudar para modo App (GPU na sessão do utilizador)…");
                    TryStopServiceQuiet();
                    KillServerProcesses();
                    await Task.Delay(800).ConfigureAwait(false);
                    StartServerProcess();
                    return await WaitForHealthAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
                }

                return true;
            }

            if (TrayPaths.IsHeadless)
            {
                if (!TryStartService())
                {
                    RunElevatedSc("start");
                }

                return await WaitForHealthAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
            }

            // Desktop GPU path: run Server.exe in the logged-on user session (not LocalSystem).
            TryStopServiceQuiet();
            if (Process.GetProcessesByName("ExLlamaSharp.Server").Length == 0)
            {
                StartServerProcess();
            }

            return await WaitForHealthAsync(TimeSpan.FromSeconds(45)).ConfigureAwait(false);
        }
        finally
        {
            _starting = false;
        }
    }

    private static bool IsServiceRunning()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.Refresh();
            return sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending;
        }
        catch
        {
            return false;
        }
    }

    private static bool IsUserSessionServerRunning()
    {
        try
        {
            foreach (var p in Process.GetProcessesByName("ExLlamaSharp.Server"))
            {
                // Session 0 = services; interactive users are typically session >= 1.
                if (p.SessionId > 0)
                {
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

    private static void TryStopServiceQuiet()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.Refresh();
            if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending)
            {
                return;
            }

            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(20));
        }
        catch
        {
            // ACL may deny; KillServerProcesses still clears orphans.
        }
    }

    private static bool TryStartService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.Refresh();
            if (sc.Status == ServiceControllerStatus.Running)
            {
                return true;
            }

            if (sc.Status is ServiceControllerStatus.StartPending)
            {
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(45));
                sc.Refresh();
                return sc.Status == ServiceControllerStatus.Running;
            }

            sc.Start();
            sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(45));
            sc.Refresh();
            return sc.Status == ServiceControllerStatus.Running;
        }
        catch
        {
            return false;
        }
    }

    private static bool TryStopService()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            sc.Refresh();
            if (sc.Status is ServiceControllerStatus.Stopped)
            {
                return true;
            }

            if (sc.Status is ServiceControllerStatus.StopPending)
            {
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
                sc.Refresh();
                return sc.Status == ServiceControllerStatus.Stopped;
            }

            sc.Stop();
            sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(45));
            sc.Refresh();
            return sc.Status == ServiceControllerStatus.Stopped;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>UAC-elevated sc.exe for start/stop when the user lacks service ACL rights.</summary>
    private static bool RunElevatedSc(string action)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"{action} {ServiceName}",
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            if (!p.WaitForExit(60_000))
            {
                return false;
            }

            // Give SCM a moment to flip state
            Thread.Sleep(1500);
            using var sc = new ServiceController(ServiceName);
            sc.Refresh();
            return action switch
            {
                "stop" => sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.StopPending,
                "start" => sc.Status is ServiceControllerStatus.Running or ServiceControllerStatus.StartPending,
                _ => p.ExitCode == 0,
            };
        }
        catch
        {
            // User cancelled UAC or sc failed
            return false;
        }
    }

    private static void StartServerProcess()
    {
        if (!File.Exists(ServerExe))
        {
            return;
        }

        if (Process.GetProcessesByName("ExLlamaSharp.Server").Length > 0)
        {
            return;
        }

        Process.Start(new ProcessStartInfo
        {
            FileName = ServerExe,
            WorkingDirectory = InstallDir,
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
    }

    private static void KillServerProcesses()
    {
        foreach (var p in Process.GetProcessesByName("ExLlamaSharp.Server"))
        {
            try
            {
                p.Kill(entireProcessTree: true);
                p.WaitForExit(10_000);
            }
            catch
            {
                // ignore
            }
        }
    }

    private static async Task<bool> IsHealthyAsync()
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
            using var res = await http.GetAsync(AdminUrl + "/health").ConfigureAwait(false);
            return res.IsSuccessStatusCode;
        }
        catch
        {
            return false;
        }
    }

    private static async Task<bool> WaitForHealthAsync(TimeSpan timeout)
    {
        var sw = Stopwatch.StartNew();
        while (sw.Elapsed < timeout)
        {
            if (await IsHealthyAsync().ConfigureAwait(false))
            {
                return true;
            }

            await Task.Delay(1000).ConfigureAwait(false);
        }

        return await IsHealthyAsync().ConfigureAwait(false);
    }

    private static void OpenUi()
    {
        try
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = AdminUrl + "/",
                UseShellExecute = true
            });
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "ExLlamaSharp", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    private void WatchRestartRequest()
    {
        try
        {
            Directory.CreateDirectory(TrayPaths.DataRoot);
            _restartWatcher = new FileSystemWatcher(TrayPaths.DataRoot, "restart.request")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            void OnRestart(object sender, FileSystemEventArgs e)
            {
                if (_intentionalStop || _starting)
                {
                    return;
                }

                _ = RestartServerAsync();
            }

            _restartWatcher.Created += OnRestart;
            _restartWatcher.Changed += OnRestart;
        }
        catch
        {
            // optional
        }
    }

    private void WatchFirewallRequest()
    {
        try
        {
            Directory.CreateDirectory(TrayPaths.DataRoot);
            _firewallWatcher = new FileSystemWatcher(TrayPaths.DataRoot, "firewall.request")
            {
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.LastWrite,
                EnableRaisingEvents = true,
            };
            void OnFirewall(object sender, FileSystemEventArgs e) => TryProcessFirewallRequest();
            _firewallWatcher.Created += OnFirewall;
            _firewallWatcher.Changed += OnFirewall;
        }
        catch
        {
            // optional
        }
    }

    private void TryProcessFirewallRequest()
    {
        if (Interlocked.Exchange(ref _firewallBusy, 1) == 1)
        {
            return;
        }

        try
        {
            var path = Path.Combine(TrayPaths.DataRoot, "firewall.request");
            if (!File.Exists(path))
            {
                return;
            }

            // Brief settle so writers finish
            Thread.Sleep(200);
            string json;
            try
            {
                json = File.ReadAllText(path);
            }
            catch
            {
                return;
            }

            bool enable = true;
            var port = 14563;
            var ruleName = "ExLlamaSharp HTTP 14563";
            try
            {
                using var doc = System.Text.Json.JsonDocument.Parse(json);
                if (doc.RootElement.TryGetProperty("enable", out var en))
                {
                    enable = en.GetBoolean();
                }

                if (doc.RootElement.TryGetProperty("port", out var p) && p.TryGetInt32(out var portVal)
                    && portVal is > 0 and < 65536)
                {
                    port = portVal;
                }

                if (doc.RootElement.TryGetProperty("rule_name", out var rn)
                    && rn.GetString() is { Length: > 0 } name)
                {
                    ruleName = name;
                }
                else
                {
                    ruleName = "ExLlamaSharp HTTP " + port;
                }
            }
            catch
            {
                return;
            }

            var ok = ApplyFirewallRuleElevated(enable, port, ruleName);
            try
            {
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }
            catch
            {
                // ignore
            }

            if (ok)
            {
                ShowBalloon(
                    "ExLlamaSharp",
                    enable
                        ? $"Firewall opened for LAN access (TCP {port})."
                        : $"Firewall rule removed (TCP {port}).");
            }
            else
            {
                ShowBalloon(
                    "ExLlamaSharp",
                    "Could not update Windows Firewall (UAC cancelled or access denied). Open TCP "
                    + port + " manually if LAN clients fail.");
            }
        }
        finally
        {
            Interlocked.Exchange(ref _firewallBusy, 0);
        }
    }

    /// <summary>UAC-elevated netsh to allow/deny inbound TCP for the API port.</summary>
    private static bool ApplyFirewallRuleElevated(bool enable, int port, string ruleName)
    {
        try
        {
            // Delete any previous rule with this name, then add when enabling.
            var safeName = ruleName.Replace("\"", "");
            var script = enable
                ? $"netsh advfirewall firewall delete rule name=\"{safeName}\" >nul 2>&1 & "
                  + $"netsh advfirewall firewall add rule name=\"{safeName}\" dir=in action=allow protocol=TCP localport={port} profile=any"
                : $"netsh advfirewall firewall delete rule name=\"{safeName}\" & exit /b 0";

            var psi = new ProcessStartInfo
            {
                FileName = "cmd.exe",
                Arguments = "/c " + script,
                UseShellExecute = true,
                Verb = "runas",
                WindowStyle = ProcessWindowStyle.Hidden,
            };
            using var p = Process.Start(psi);
            if (p is null)
            {
                return false;
            }

            return p.WaitForExit(60_000) && p.ExitCode == 0;
        }
        catch
        {
            // User cancelled UAC
            return false;
        }
    }

    private static void OpenDataFolder()
    {
        Directory.CreateDirectory(TrayPaths.DataRoot);
        Process.Start(new ProcessStartInfo { FileName = TrayPaths.DataRoot, UseShellExecute = true });
    }

    private async Task RefreshStatusAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            string line = "Service: …";
            Icon icon = _iconOff;
            var startOn = true;
            var stopOn = false;

            var serviceRunning = false;
            var serviceStatus = "not installed";
            try
            {
                using var sc = new ServiceController(ServiceName);
                sc.Refresh();
                serviceStatus = sc.Status.ToString();
                serviceRunning = sc.Status == ServiceControllerStatus.Running;
            }
            catch
            {
                // service may be missing
            }

            var processRunning = Process.GetProcessesByName("ExLlamaSharp.Server").Length > 0;
            var healthyJson = (string?)null;
            if (serviceRunning || processRunning)
            {
                try
                {
                    using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                    healthyJson = await http.GetStringAsync(AdminUrl + "/health").ConfigureAwait(false);
                }
                catch
                {
                    healthyJson = null;
                }
            }

            if (healthyJson is not null)
            {
                startOn = false;
                stopOn = true;
                var degraded = healthyJson.Contains("\"status\":\"degraded\"", StringComparison.Ordinal);
                var healthy = healthyJson.Contains("\"status\":\"healthy\"", StringComparison.Ordinal);
                var mode = serviceRunning ? "Service" : "App";
                if (healthy && !degraded)
                {
                    line = $"{mode}: Running · Healthy";
                    icon = _iconOk;
                }
                else
                {
                    line = $"{mode}: Running · Degraded (no model?)";
                    icon = _iconWarn;
                }
            }
            else if (serviceRunning || processRunning)
            {
                startOn = false;
                stopOn = true;
                line = serviceRunning
                    ? $"Service: {serviceStatus} · UI unreachable"
                    : "App: starting / UI unreachable";
                icon = _iconWarn;
            }
            else
            {
                line = serviceStatus == "not installed"
                    ? "Server: stopped"
                    : $"Service: {serviceStatus}";
                icon = _iconOff;

                if (!_intentionalStop && !_starting)
                {
                    if (_lastStableUtc != DateTime.MinValue
                        && (DateTime.UtcNow - _lastStableUtc) > TimeSpan.FromMinutes(15))
                    {
                        _autoRecoverAttempts = 0;
                    }

                    var delay = _autoRecoverAttempts switch
                    {
                        0 => TimeSpan.FromSeconds(5),
                        1 => TimeSpan.FromSeconds(20),
                        2 => TimeSpan.FromSeconds(60),
                        _ => TimeSpan.FromMinutes(5),
                    };
                    if ((DateTime.UtcNow - _lastAutoRecoverUtc) > delay)
                    {
                        _lastAutoRecoverUtc = DateTime.UtcNow;
                        _autoRecoverAttempts++;
                        line = $"Recovering (attempt {_autoRecoverAttempts}, backoff {delay.TotalSeconds:0}s)…";
                        icon = _iconWarn;
                        _ = EnsureServerRunningAsync().ContinueWith(async _ =>
                        {
                            await RefreshStatusAsync().ConfigureAwait(false);
                            if (await IsHealthyAsync().ConfigureAwait(false))
                            {
                                _autoRecoverAttempts = 0;
                                _lastStableUtc = DateTime.UtcNow;
                                ShowBalloon("ExLlamaSharp", "Servidor recuperado automaticamente.");
                            }
                        }, TaskScheduler.Default);
                    }
                }
            }

            if (healthyJson is not null)
            {
                _lastStableUtc = DateTime.UtcNow;
                if ((DateTime.UtcNow - _lastStableUtc) > TimeSpan.FromMinutes(15)
                    || _autoRecoverAttempts > 0)
                {
                    _autoRecoverAttempts = 0;
                }
            }

            PostUi(() =>
            {
                _statusItem.Text = line;
                _startItem.Enabled = startOn;
                _stopItem.Enabled = stopOn;
                _restartItem.Enabled = true;
                var tip = "ExLlamaSharp\n" + line;
                if (tip.Length > 63)
                {
                    tip = tip[..60] + "…";
                }

                _tray.Text = tip;
                _tray.Icon = icon;
            });
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void PostUi(Action action)
    {
        _ui.Post(_ =>
        {
            try
            {
                action();
            }
            catch
            {
                // ignore UI races during shutdown
            }
        }, null);
    }

    protected override void ExitThreadCore()
    {
        _timer.Stop();
        _restartWatcher?.Dispose();
        _firewallWatcher?.Dispose();
        _tray.Visible = false;
        _tray.Dispose();
        _iconOk.Dispose();
        _iconWarn.Dispose();
        _iconOff.Dispose();
        _bmpOk.Dispose();
        _bmpWarn.Dispose();
        _bmpOff.Dispose();
        base.ExitThreadCore();
    }
}
