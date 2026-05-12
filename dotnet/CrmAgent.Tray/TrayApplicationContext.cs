using System.ServiceProcess;

namespace CrmAgent.Tray;

/// <summary>
/// Drives the system tray icon lifecycle.
/// On first run (no config) immediately shows <see cref="ConnectForm"/>.
/// A background timer refreshes the service status every 10 seconds.
/// Left-click opens the <see cref="StatusForm"/> popup.
/// </summary>
public sealed class TrayApplicationContext : ApplicationContext
{

    private readonly NotifyIcon _notifyIcon;
    private readonly ToolStripMenuItem _statusMenuItem;
    private readonly ToolStripMenuItem _updateMenuItem;
    private readonly UpdateService _updateService;
    private readonly System.Windows.Forms.Timer _pollTimer;
    private StatusForm? _statusForm;
    private ConnectForm? _connectForm;
    private readonly bool _showStatusAfterUpdate;

    public TrayApplicationContext(string? updatedFromVersion = null)
    {
        _statusMenuItem = new ToolStripMenuItem("Checking status…") { Enabled = false };
        _updateMenuItem = new ToolStripMenuItem("Check for Updates…", null, async (_, _) => await CheckForUpdateManual());

        var headerItem = new ToolStripMenuItem("GDATA CRM Agent")
        {
            Enabled = false,
            Font = Theme.SubHead,
        };

        var menu = new ContextMenuStrip
        {
            BackColor = Theme.Surface,
            ForeColor = Theme.TextPrimary,
            ShowImageMargin = false,
            Renderer = new DarkToolStripRenderer(),
        };
        menu.Items.Add(headerItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add(_statusMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Configure…", null, (_, _) => ShowConnectForm());
        menu.Items.Add("Open Log Folder", null, (_, _) => OpenLogFolder());
        menu.Items.Add(_updateMenuItem);
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Uninstall…", null, (_, _) => ConfirmAndUninstall());
        menu.Items.Add(new ToolStripSeparator());
        menu.Items.Add("Exit", null, (_, _) => Exit());

        _notifyIcon = new NotifyIcon
        {
            Icon = LoadAppIcon(),
            Text = "GDATA CRM Agent",
            Visible = true,
            ContextMenuStrip = menu,
        };
        _notifyIcon.MouseClick += OnTrayClick;

        _pollTimer = new System.Windows.Forms.Timer { Interval = 10_000 };
        _pollTimer.Tick += (_, _) => RefreshStatus();
        _pollTimer.Start();

        // Defer first-run check until after the message loop starts
        Application.Idle += OnFirstIdle;

        // Start background update checker
        _updateService = new UpdateService();
        _updateService.UpdateReady += OnUpdateReady;
        _updateService.DownloadProgress += OnDownloadProgress;
        _updateService.DownloadFailed += OnDownloadFailed;
        _updateService.ApplyingUpdate += OnApplyingUpdate;
        _updateService.Start();

        // If relaunched after a successful update, show a success toast
        // and defer opening the status window until the message loop is running
        if (updatedFromVersion is not null)
        {
            _notifyIcon.BalloonTipTitle = "Update Complete";
            _notifyIcon.BalloonTipText = $"GDATA CRM Agent updated to {UpdateService.CurrentVersion} successfully.";
            _notifyIcon.ShowBalloonTip(5_000);
            _showStatusAfterUpdate = true;
        }
    }

    private void OnUpdateReady(string version, string msiPath)
    {
        _updateMenuItem.Text = $"Update to {version}";
        _updateMenuItem.Font = new Font(_updateMenuItem.Font, FontStyle.Bold);
        _updateMenuItem.Click -= OnApplyUpdate;
        _updateMenuItem.Click += OnApplyUpdate;
        _notifyIcon.BalloonTipTitle = "Update Available";
        _notifyIcon.BalloonTipText = $"GDATA CRM Agent {version} is ready to install.";
        _notifyIcon.ShowBalloonTip(5_000);
        // Notify the StatusForm if it's open
        _statusForm?.SetUpdateAvailable(version);
    }

    private void OnDownloadProgress(long bytesReceived, long totalBytes)
    {
        _statusForm?.SetDownloadProgress(bytesReceived, totalBytes);
    }

    private void OnDownloadFailed()
    {
        _statusForm?.ResetDownloadProgress();
    }

    private void OnApplyUpdate(object? sender, EventArgs e)
    {
        _updateService.ApplyUpdate(_notifyIcon);
    }

    private void OnApplyingUpdate()
    {
        // Close the StatusForm cleanly before the MSI kills the process
        if (_statusForm is { Visible: true })
        {
            _statusForm.Close();
            _statusForm = null;
        }
    }

    private async Task CheckForUpdateManual()
    {
        _updateMenuItem.Text = "Checking…";
        _updateMenuItem.Enabled = false;
        try
        {
            await _updateService.CheckNowAsync();
            // If no update was found (UpdateReady didn't fire), reset text
            if (_updateService.AvailableVersion is null)
            {
                _updateMenuItem.Text = "No Updates Available";
                // Reset back to normal after a few seconds
                var resetTimer = new System.Windows.Forms.Timer { Interval = 3_000 };
                resetTimer.Tick += (_, _) =>
                {
                    _updateMenuItem.Text = "Check for Updates…";
                    resetTimer.Stop();
                    resetTimer.Dispose();
                };
                resetTimer.Start();
            }
        }
        catch
        {
            _updateMenuItem.Text = "Update Check Failed";
        }
        finally
        {
            _updateMenuItem.Enabled = true;
        }
    }

    private void OnFirstIdle(object? sender, EventArgs e)
    {
        Application.Idle -= OnFirstIdle;
        RefreshStatus();
        if (!ConfigStore.IsConfigured())
            ShowConnectForm();
        else if (_showStatusAfterUpdate)
            ShowStatusForm();
    }

    private void RefreshStatus()
    {
        try
        {
            var status = ServiceManager.GetStatus();
            var (text, tip) = status switch
            {
                ServiceControllerStatus.Running => ("● Running", "GDATA CRM Agent | Running"),
                ServiceControllerStatus.Stopped => ("○ Stopped", "GDATA CRM Agent | Stopped"),
                ServiceControllerStatus.StartPending => ("◌ Starting…", "GDATA CRM Agent | Starting"),
                ServiceControllerStatus.StopPending => ("◌ Stopping…", "GDATA CRM Agent | Stopping"),
                _ => ("? Unknown", "GDATA CRM Agent | Unknown"),
            };
            _statusMenuItem.Text = text;
            // NotifyIcon.Text is capped at 63 characters
            _notifyIcon.Text = tip.Length > 63 ? tip[..63] : tip;
        }
        catch { /* never crash the tray */ }
    }

    private void OnTrayClick(object? sender, MouseEventArgs e)
    {
        if (e.Button != MouseButtons.Left) return;
        ShowStatusForm();
    }

    private void ShowStatusForm()
    {
        if (_statusForm is { Visible: true })
        {
            _statusForm.BringToFront();
            return;
        }

        _statusForm = new StatusForm(_updateService);
        _statusForm.FormClosed += (_, _) => _statusForm = null;
        _statusForm.ConfigureRequested += ShowConnectForm;
        _statusForm.UpdateRequested += () => _updateService.ApplyUpdate(_notifyIcon);
        _statusForm.Show();
    }

    private void ShowConnectForm()
    {
        if (_connectForm is { Visible: true })
        {
            _connectForm.BringToFront();
            _connectForm.Activate();
            return;
        }

        _connectForm = new ConnectForm();
        _connectForm.FormClosed += (_, _) =>
        {
            var started = _connectForm.ServiceStarted;
            _connectForm.Dispose();
            _connectForm = null;
            RefreshStatus();
            if (started)
                ShowStatusForm();
        };
        _connectForm.Show();
    }

    private static void OpenLogFolder()
    {
        var path = ConfigStore.ConfigDirectory;
        if (Directory.Exists(path))
            System.Diagnostics.Process.Start("explorer.exe", path);
    }

    private void ConfirmAndUninstall()
    {
        var result = MessageBox.Show(
            "Are you sure you want to uninstall GDATA CRM Agent?\n\nThis will stop the service and remove the application.",
            "Uninstall GDATA CRM Agent",
            MessageBoxButtons.YesNo,
            MessageBoxIcon.Warning,
            MessageBoxDefaultButton.Button2);

        if (result != DialogResult.Yes)
            return;

        if (!ServiceManager.LaunchUninstaller())
        {
            MessageBox.Show(
                "Could not find the uninstaller. Please use 'Add or Remove Programs' in Windows Settings instead.",
                "Uninstall",
                MessageBoxButtons.OK,
                MessageBoxIcon.Information);
            return;
        }

        // The uninstaller will kill this process, but exit gracefully just in case.
        Exit();
    }

    private void Exit()
    {
        _pollTimer.Stop();
        _notifyIcon.Visible = false;
        Application.Exit();
    }

    /// <summary>
    /// Loads the application icon embedded in the executable by the
    /// &lt;ApplicationIcon&gt; build property. Falls back to the generic
    /// application icon if the resource is missing.
    /// </summary>
    internal static Icon LoadAppIcon()
    {
        try
        {
            // Icon.ExtractAssociatedIcon pulls the first icon group resource
            // embedded in the exe (set via <ApplicationIcon> in the .csproj).
            var exePath = Environment.ProcessPath;
            if (exePath is not null)
            {
                var icon = Icon.ExtractAssociatedIcon(exePath);
                if (icon is not null)
                    return icon;
            }
        }
        catch { /* fall through */ }
        return SystemIcons.Application;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _updateService.Dispose();
            _pollTimer.Dispose();
            _notifyIcon.Dispose();
        }
        base.Dispose(disposing);
    }
}

/// <summary>
/// Custom renderer that paints ContextMenuStrip items with the dark theme colours.
/// </summary>
file sealed class DarkToolStripRenderer : ToolStripProfessionalRenderer
{
    public DarkToolStripRenderer() : base(new DarkColorTable()) { }

    protected override void OnRenderMenuItemBackground(ToolStripItemRenderEventArgs e)
    {
        var rect = new Rectangle(Point.Empty, e.Item.Size);
        var color = e.Item.Selected && e.Item.Enabled ? Theme.SurfaceLight : Theme.Surface;
        using var brush = new SolidBrush(color);
        e.Graphics.FillRectangle(brush, rect);
    }

    protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
    {
        e.TextColor = e.Item.Enabled ? Theme.TextPrimary : Theme.TextDim;
        base.OnRenderItemText(e);
    }

    protected override void OnRenderSeparator(ToolStripSeparatorRenderEventArgs e)
    {
        var y = e.Item.Height / 2;
        using var pen = new Pen(Theme.Border);
        e.Graphics.DrawLine(pen, 0, y, e.Item.Width, y);
    }

    protected override void OnRenderToolStripBackground(ToolStripRenderEventArgs e)
    {
        using var brush = new SolidBrush(Theme.Surface);
        e.Graphics.FillRectangle(brush, e.AffectedBounds);
    }

    protected override void OnRenderToolStripBorder(ToolStripRenderEventArgs e)
    {
        using var pen = new Pen(Theme.Border);
        var rect = new Rectangle(0, 0, e.AffectedBounds.Width - 1, e.AffectedBounds.Height - 1);
        e.Graphics.DrawRectangle(pen, rect);
    }

    private sealed class DarkColorTable : ProfessionalColorTable
    {
        public override Color MenuBorder => Theme.Border;
        public override Color MenuItemBorder => Color.Transparent;
        public override Color MenuItemSelected => Theme.SurfaceLight;
        public override Color MenuStripGradientBegin => Theme.Surface;
        public override Color MenuStripGradientEnd => Theme.Surface;
        public override Color MenuItemSelectedGradientBegin => Theme.SurfaceLight;
        public override Color MenuItemSelectedGradientEnd => Theme.SurfaceLight;
        public override Color ImageMarginGradientBegin => Theme.Surface;
        public override Color ImageMarginGradientMiddle => Theme.Surface;
        public override Color ImageMarginGradientEnd => Theme.Surface;
        public override Color SeparatorDark => Theme.Border;
        public override Color SeparatorLight => Theme.Border;
    }
}
