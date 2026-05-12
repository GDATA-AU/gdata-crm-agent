using System.ServiceProcess;

namespace CrmAgent.Tray;

/// <summary>
/// Small status popup that appears when the user left-clicks the tray icon.
/// Shows service status, provides quick Start/Stop and Configure actions,
/// and displays a live activity feed from the agent log.
/// </summary>
public sealed class StatusForm : Form
{
    /// <summary>Raised when the user clicks Configure, so the tray context can open the ConnectForm.</summary>
    public event Action? ConfigureRequested;

    /// <summary>Raised when the user clicks the Update button, so the tray context can orchestrate the update.</summary>
    public event Action? UpdateRequested;

    private readonly Label _statusLabel;
    private readonly Panel _statusDot;
    private readonly Label _portalLabel;
    private readonly Label _versionLabel;
    private readonly Button _startStopBtn;
    private readonly Button _updateBtn;
    private readonly ProgressBar _downloadProgress;
    private readonly ActivityFeedPanel _activityFeed;
    private readonly System.Windows.Forms.Timer _refreshTimer;
    private readonly LogTailer _logTailer = new();
    private readonly UpdateService _updateService;

    public StatusForm(UpdateService updateService)
    {
        _updateService = updateService;
        Text = "GDATA CRM Agent";
        Icon = TrayApplicationContext.LoadAppIcon();
        FormBorderStyle = FormBorderStyle.Sizable;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(560, 480);
        Size = new Size(680, 580);
        Theme.ApplyToForm(this);

        // ── Status indicator row ──
        _statusDot = new Panel
        {
            Size = new Size(14, 14),
            Margin = new Padding(0, 8, 10, 0),
            BackColor = Theme.TextDim,
        };
        _statusDot.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            using var brush = new SolidBrush(_statusDot.BackColor);
            e.Graphics.Clear(Theme.Surface);
            e.Graphics.FillEllipse(brush, 0, 0, 13, 13);
        };

        _statusLabel = new Label
        {
            AutoSize = true,
            Font = Theme.Heading,
            ForeColor = Theme.TextPrimary,
            Margin = new Padding(0, 0, 0, 0),
        };

        var statusRow = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            FlowDirection = FlowDirection.LeftToRight,
            BackColor = Theme.Surface,
            Margin = new Padding(0),
        };
        statusRow.Controls.Add(_statusDot);
        statusRow.Controls.Add(_statusLabel);

        _portalLabel = new Label
        {
            AutoSize = true,
            ForeColor = Theme.TextSecondary,
            Font = Theme.Body,
            Margin = new Padding(0, 6, 0, 0),
        };

        _versionLabel = new Label
        {
            AutoSize = true,
            ForeColor = Theme.TextDim,
            Font = Theme.Body,
            Text = $"Version: {UpdateService.CurrentVersion}",
            Margin = new Padding(0, 2, 0, 0),
        };

        _startStopBtn = new Button { AutoSize = true };
        Theme.StylePrimary(_startStopBtn);

        var configBtn = new Button { Text = "Configure…", AutoSize = true };
        Theme.StyleSecondary(configBtn);

        var openLogsBtn = new Button { Text = "Open Logs", AutoSize = true };
        Theme.StyleSecondary(openLogsBtn);

        _updateBtn = new Button { Text = "Check for Updates", AutoSize = true };
        Theme.StyleSecondary(_updateBtn);
        if (_updateService.AvailableVersion is not null)
        {
            _updateBtn.Text = $"Update to {_updateService.AvailableVersion}";
            Theme.StylePrimary(_updateBtn);
        }

        _downloadProgress = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = 0,
            Height = 6,
            Width = 200,
            Visible = false,
            Margin = new Padding(0, 8, 0, 0),
            Style = ProgressBarStyle.Continuous,
        };

        _startStopBtn.Click += OnStartStop;
        configBtn.Click += (_, _) =>
        {
            Close();
            ConfigureRequested?.Invoke();
        };
        openLogsBtn.Click += OnOpenLogs;
        _updateBtn.Click += OnUpdateClick;

        var btnRow = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            Margin = new Padding(0, 14, 0, 0),
            BackColor = Theme.Surface,
        };
        btnRow.Controls.Add(_startStopBtn);
        btnRow.Controls.Add(configBtn);
        btnRow.Controls.Add(openLogsBtn);
        btnRow.Controls.Add(_updateBtn);

        // -- Activity feed --
        var activityLabel = new Label
        {
            Text = "RECENT ACTIVITY",
            AutoSize = true,
            Font = Theme.SubHead,
            ForeColor = Theme.TextSecondary,
            Margin = new Padding(0, 14, 0, 8),
        };

        _activityFeed = new ActivityFeedPanel
        {
            Dock = DockStyle.Fill,
        };

        // -- Header card (status + buttons) --
        var headerCard = new Panel
        {
            Dock = DockStyle.Top,
            AutoSize = true,
            BackColor = Theme.Surface,
            Padding = new Padding(24, 20, 24, 20),
        };

        var headerLayout = new FlowLayoutPanel
        {
            FlowDirection = FlowDirection.TopDown,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            WrapContents = false,
            BackColor = Theme.Surface,
        };
        headerLayout.Controls.Add(statusRow);
        headerLayout.Controls.Add(_portalLabel);
        headerLayout.Controls.Add(_versionLabel);
        headerLayout.Controls.Add(btnRow);
        headerLayout.Controls.Add(_downloadProgress);
        headerCard.Controls.Add(headerLayout);

        // -- Activity label sits between header and log --
        var activityHeader = new Panel
        {
            Dock = DockStyle.Top,
            Height = activityLabel.PreferredHeight + 26,
            Padding = new Padding(24, 18, 24, 0),
            BackColor = Theme.Background,
        };
        activityHeader.Controls.Add(activityLabel);

        // -- Activity panel fills remaining space --
        var activityPanel = new Panel
        {
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 0, 24, 20),
            BackColor = Theme.Background,
        };
        activityPanel.Controls.Add(_activityFeed);

        Controls.Add(activityPanel);
        Controls.Add(activityHeader);
        Controls.Add(headerCard);

        RefreshDisplay();
        LoadActivity();

        _refreshTimer = new System.Windows.Forms.Timer { Interval = 3_000 };
        _refreshTimer.Tick += (_, _) =>
        {
            RefreshDisplay();
            LoadActivity();
        };
        _refreshTimer.Start();
    }

    private void RefreshDisplay()
    {
        var status = ServiceManager.GetStatus();
        var settings = ConfigStore.Load();

        var (text, color) = status switch
        {
            ServiceControllerStatus.Running => ("Running", Theme.Success),
            ServiceControllerStatus.Stopped => ("Stopped", Theme.Error),
            ServiceControllerStatus.StartPending => ("Starting…", Theme.Warning),
            ServiceControllerStatus.StopPending => ("Stopping…", Theme.Warning),
            _ => ("Unknown", Theme.TextDim),
        };
        _statusLabel.Text = text;
        _statusLabel.ForeColor = color;
        _statusDot.BackColor = color;
        _statusDot.Invalidate();

        _portalLabel.Text = !string.IsNullOrEmpty(settings?.PortalUrl)
            ? $"Portal: {settings!.PortalUrl}"
            : "Portal: not configured";

        _startStopBtn.Text = status == ServiceControllerStatus.Running ? "Stop" : "Start";
    }

    private void LoadActivity()
    {
        var entries = _logTailer.ReadNewEntries();
        if (entries.Count == 0) return;
        _activityFeed.AddEntries(entries);
    }


    private void OnStartStop(object? sender, EventArgs e)
    {
        try
        {
            if (ServiceManager.GetStatus() == ServiceControllerStatus.Running)
                ServiceManager.Stop();
            else
                ServiceManager.Start();
        }
        catch (Exception ex)
        {
            MessageBox.Show(ex.Message, "Service Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
        }
        RefreshDisplay();
    }

    private static void OnOpenLogs(object? sender, EventArgs e)
    {
        var logDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "GDATA CRM Agent",
            "logs");
        Directory.CreateDirectory(logDir);
        System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = logDir,
            UseShellExecute = true,
        });
    }

    private async void OnUpdateClick(object? sender, EventArgs e)
    {
        if (_updateService.AvailableVersion is not null)
        {
            UpdateRequested?.Invoke();
            return;
        }

        _updateBtn.Text = "Checking…";
        _updateBtn.Enabled = false;
        try
        {
            await _updateService.CheckNowAsync();
            if (_updateService.AvailableVersion is not null)
            {
                SetUpdateAvailable(_updateService.AvailableVersion);
            }
            else
            {
                _updateBtn.Text = "Up to Date";
                var resetTimer = new System.Windows.Forms.Timer { Interval = 3_000 };
                resetTimer.Tick += (_, _) =>
                {
                    _updateBtn.Text = "Check for Updates";
                    resetTimer.Stop();
                    resetTimer.Dispose();
                };
                resetTimer.Start();
            }
        }
        catch
        {
            _updateBtn.Text = "Check Failed";
        }
        finally
        {
            _updateBtn.Enabled = true;
        }
    }

    /// <summary>Called by TrayApplicationContext when UpdateService fires UpdateReady.</summary>
    internal void SetUpdateAvailable(string version)
    {
        if (InvokeRequired)
        {
            Invoke(() => SetUpdateAvailable(version));
            return;
        }
        _downloadProgress.Visible = false;
        _updateBtn.Text = $"Update to {version}";
        _updateBtn.Enabled = true;
        Theme.StylePrimary(_updateBtn);
    }

    /// <summary>Called by TrayApplicationContext when a download fails to reset the UI.</summary>
    internal void ResetDownloadProgress()
    {
        if (InvokeRequired)
        {
            BeginInvoke(ResetDownloadProgress);
            return;
        }

        _downloadProgress.Visible = false;
        _downloadProgress.Value = 0;
        _updateBtn.Text = "Check for Updates";
        _updateBtn.Enabled = true;
    }

    /// <summary>Called by TrayApplicationContext when UpdateService reports download progress.</summary>
    internal void SetDownloadProgress(long bytesReceived, long totalBytes)
    {
        if (InvokeRequired)
        {
            BeginInvoke(() => SetDownloadProgress(bytesReceived, totalBytes));
            return;
        }

        if (!_downloadProgress.Visible)
        {
            _downloadProgress.Visible = true;
            _updateBtn.Enabled = false;
        }

        if (totalBytes > 0)
        {
            var pct = (int)(bytesReceived * 100 / totalBytes);
            _downloadProgress.Style = ProgressBarStyle.Continuous;
            _downloadProgress.Value = Math.Min(pct, 100);
            var mb = bytesReceived / (1024.0 * 1024.0);
            var totalMb = totalBytes / (1024.0 * 1024.0);
            _updateBtn.Text = $"Downloading… {mb:F1} / {totalMb:F1} MB";
        }
        else
        {
            _downloadProgress.Style = ProgressBarStyle.Marquee;
            var mb = bytesReceived / (1024.0 * 1024.0);
            _updateBtn.Text = $"Downloading… {mb:F1} MB";
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _refreshTimer.Dispose();
        base.Dispose(disposing);
    }
}
