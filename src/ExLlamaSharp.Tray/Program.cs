using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.ServiceProcess;

namespace ExLlamaSharp.Tray;

internal static class Program
{
    private const string MutexName = "Local\\ExLlamaSharp.Tray.SingleInstance";

    [STAThread]
    private static void Main()
    {
        using var mutex = new Mutex(true, MutexName, out var created);
        if (!created)
        {
            return;
        }

        ApplicationConfiguration.Initialize();
        Application.Run(new TrayApplicationContext());
    }
}

internal sealed class TrayApplicationContext : ApplicationContext
{
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

    public TrayApplicationContext()
    {
        try
        {
            _ui = SynchronizationContext.Current ?? new SynchronizationContext();
            EnsureUserAutostart();

            // Keep bitmaps alive — FromHandle icons alias them.
            _bmpOk = CreateIconBitmap(Color.FromArgb(34, 197, 94));
            _bmpWarn = CreateIconBitmap(Color.FromArgb(234, 179, 8));
            _bmpOff = CreateIconBitmap(Color.FromArgb(148, 163, 184));

            _iconOk = Icon.FromHandle(_bmpOk.GetHicon());
            _iconWarn = Icon.FromHandle(_bmpWarn.GetHicon());
            _iconOff = Icon.FromHandle(_bmpOff.GetHicon());

            _statusItem = new ToolStripMenuItem("Status: …") { Enabled = false };
            _startItem = new ToolStripMenuItem("Start service", null, (_, _) => ControlService("start"));
            _stopItem = new ToolStripMenuItem("Stop service", null, (_, _) => ControlService("stop"));
            _restartItem = new ToolStripMenuItem("Restart service", null, (_, _) => ControlService("restart"));

            var menu = new ContextMenuStrip();
            menu.Items.Add(_statusItem);
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Open Admin UI", null, (_, _) => OpenUi());
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
            _tray.DoubleClick += (_, _) => OpenUi();

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
            _ = RefreshStatusAsync();
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

    private static string AdminUrl =>
        Environment.GetEnvironmentVariable("EXLLAMASHARP_URL") is { Length: > 0 } u
            ? u.TrimEnd('/')
            : "http://127.0.0.1:14563";

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

    private static void OpenDataFolder()
    {
        var path = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "ExLlamaSharp");
        Directory.CreateDirectory(path);
        Process.Start(new ProcessStartInfo { FileName = path, UseShellExecute = true });
    }

    private static void ControlService(string action)
    {
        try
        {
            using var sc = new ServiceController("ExLlamaSharp");
            sc.Refresh();
            if (action is "stop" or "restart" && sc.Status != ServiceControllerStatus.Stopped)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(40));
            }

            if (action is "start" or "restart")
            {
                sc.Refresh();
                if (sc.Status != ServiceControllerStatus.Running)
                {
                    sc.Start();
                    sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(40));
                }
            }
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                "Could not control the Windows service (admin rights may be required).\n\n" + ex.Message,
                "ExLlamaSharp",
                MessageBoxButtons.OK,
                MessageBoxIcon.Warning);
        }
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
            try
            {
                using var sc = new ServiceController("ExLlamaSharp");
                sc.Refresh();
                if (sc.Status != ServiceControllerStatus.Running)
                {
                    line = $"Service: {sc.Status}";
                    icon = _iconOff;
                }
                else
                {
                    startOn = false;
                    stopOn = true;
                    try
                    {
                        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
                        var json = await http.GetStringAsync(AdminUrl + "/health").ConfigureAwait(false);
                        var degraded = json.Contains("\"status\":\"degraded\"", StringComparison.Ordinal);
                        var healthy = json.Contains("\"status\":\"healthy\"", StringComparison.Ordinal);
                        if (healthy && !degraded)
                        {
                            line = "Service: Running · Healthy";
                            icon = _iconOk;
                        }
                        else
                        {
                            line = "Service: Running · Degraded (no model?)";
                            icon = _iconWarn;
                        }
                    }
                    catch
                    {
                        line = "Service: Running · UI unreachable";
                        icon = _iconWarn;
                    }
                }
            }
            catch
            {
                line = "Service: not installed / unreachable";
                icon = _iconOff;
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
