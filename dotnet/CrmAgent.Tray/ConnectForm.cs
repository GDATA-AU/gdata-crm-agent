using System.Net.Http.Headers;

namespace CrmAgent.Tray;

/// <summary>
/// First-run / reconfigure form. Collects Portal URL, API key, and Azure
/// Storage connection string, validates the portal connection, then saves
/// config and starts the service.
/// </summary>
public sealed class ConnectForm : Form
{
    /// <summary>True when the user saved config and the service was started successfully.</summary>
    public bool ServiceStarted { get; private set; }

    private readonly TextBox _urlBox;
    private readonly TextBox _apiKeyBox;
    private readonly TextBox _azureBox;
    private readonly Button _testBtn;
    private readonly Button _saveBtn;
    private readonly Label _testStatus;

    public ConnectForm()
    {
        Text = "GDATA CRM Agent – Setup";
        Icon = TrayApplicationContext.LoadAppIcon();
        FormBorderStyle = FormBorderStyle.Sizable;
        MaximizeBox = false;
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterScreen;
        MinimumSize = new Size(480, 420);
        Size = new Size(580, 460);
        Theme.ApplyToForm(this);

        _urlBox = new TextBox { Dock = DockStyle.Fill };
        _apiKeyBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        _azureBox = new TextBox { Dock = DockStyle.Fill, UseSystemPasswordChar = true };
        Theme.StyleTextBox(_urlBox);
        Theme.StyleTextBox(_apiKeyBox);
        Theme.StyleTextBox(_azureBox);

        _testBtn = new Button { Text = "Test Connection", AutoSize = true };
        Theme.StyleSecondary(_testBtn);

        _saveBtn = new Button { Text = "Save && Start Service", AutoSize = true, Enabled = false };
        Theme.StylePrimary(_saveBtn);

        _testStatus = new Label
        {
            AutoSize = true,
            Text = string.Empty,
            Padding = new Padding(4, 6, 0, 0),
            ForeColor = Theme.TextSecondary,
        };

        // -- Title --
        var title = new Label
        {
            Text = "Connect to Portal",
            Font = Theme.Heading,
            ForeColor = Theme.TextPrimary,
            AutoSize = true,
            Margin = new Padding(0, 0, 0, 4),
        };
        var subtitle = new Label
        {
            Text = "Enter the credentials provided by your GDATA Customer Portal administrator.",
            Font = Theme.Small,
            ForeColor = Theme.TextSecondary,
            AutoSize = true,
            MaximumSize = new Size(500, 0),
            Margin = new Padding(0, 0, 0, 16),
        };

        var layout = new TableLayoutPanel
        {
            ColumnCount = 2,
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            Dock = DockStyle.Fill,
            Padding = new Padding(24, 20, 24, 8),
            BackColor = Theme.Background,
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 180));
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        // Explicit row heights so multi-line labels ("Azure Storage\nConnection String") aren't clipped
        for (var i = 0; i < 10; i++)
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));

        // Title spans both columns
        layout.Controls.Add(title, 0, 0);
        layout.SetColumnSpan(title, 2);
        layout.Controls.Add(subtitle, 0, 1);
        layout.SetColumnSpan(subtitle, 2);

        // Separator
        var sep = new Panel
        {
            Height = 1,
            Dock = DockStyle.Top,
            BackColor = Theme.Border,
            Margin = new Padding(0, 0, 0, 12),
        };
        layout.Controls.Add(sep, 0, 2);
        layout.SetColumnSpan(sep, 2);

        AddRow(layout, 3, "Portal URL", _urlBox);
        AddRow(layout, 4, "Agent API Key", _apiKeyBox);
        AddRow(layout, 5, "Azure Storage\nConnection String", _azureBox);

        // Test row
        var testRow = new FlowLayoutPanel
        {
            AutoSize = true,
            WrapContents = false,
            BackColor = Theme.Background,
            Margin = new Padding(0, 8, 0, 0),
        };
        testRow.Controls.Add(_testBtn);
        testRow.Controls.Add(_testStatus);
        layout.Controls.Add(new Label(), 0, 6);
        layout.Controls.Add(testRow, 1, 6);

        // Separator + Save button
        var sep2 = new Panel
        {
            Height = 1,
            Dock = DockStyle.Top,
            BackColor = Theme.Border,
            Margin = new Padding(0, 12, 0, 12),
        };
        layout.Controls.Add(sep2, 0, 7);
        layout.SetColumnSpan(sep2, 2);

        layout.Controls.Add(new Label(), 0, 8);
        layout.Controls.Add(_saveBtn, 1, 8);
        // Bottom padding row
        layout.Controls.Add(new Label { Height = 12 }, 0, 9);

        Controls.Add(layout);

        // Pre-populate from existing config (reconfigure scenario)
        var existing = ConfigStore.Load();
        if (existing is not null)
        {
            _urlBox.Text = existing.PortalUrl;
            _apiKeyBox.Text = existing.AgentApiKey;
            _azureBox.Text = existing.AzureStorageConnectionString;
        }

        _testBtn.Click += OnTestConnection;
        _saveBtn.Click += OnSave;
    }

    private void AddRow(TableLayoutPanel layout, int row, string labelText, Control control)
    {
        var label = new Label
        {
            Text = labelText,
            AutoSize = true,
            Anchor = AnchorStyles.Right,
            TextAlign = ContentAlignment.MiddleRight,
            ForeColor = Theme.TextSecondary,
            Font = Theme.Body,
            Margin = new Padding(0, 6, 6, 6),
        };
        layout.Controls.Add(label, 0, row);
        layout.Controls.Add(control, 1, row);
    }

    private async void OnTestConnection(object? sender, EventArgs e)
    {
        _testBtn.Enabled = false;
        _saveBtn.Enabled = false;
        _testStatus.Text = "Testing…";
        _testStatus.ForeColor = Theme.TextSecondary;

        var url = _urlBox.Text.Trim().TrimEnd('/');
        var key = _apiKeyBox.Text.Trim();

        if (string.IsNullOrEmpty(url) || !Uri.IsWellFormedUriString(url, UriKind.Absolute))
        {
            _testStatus.Text = "✗ Enter a valid URL.";
            _testStatus.ForeColor = Theme.Error;
            _testBtn.Enabled = true;
            return;
        }

        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key);
            var response = await http.GetAsync($"{url}/api/agent/jobs");

            if (response.StatusCode == System.Net.HttpStatusCode.Unauthorized)
            {
                _testStatus.Text = "✗ Invalid API key.";
                _testStatus.ForeColor = Theme.Error;
            }
            else if (!response.IsSuccessStatusCode)
            {
                _testStatus.Text = $"✗ Server error ({(int)response.StatusCode}).";
                _testStatus.ForeColor = Theme.Error;
            }
            else
            {
                // 200 (job available), 204 (no jobs) — both confirm auth succeeded
                _testStatus.Text = "✓ Connected.";
                _testStatus.ForeColor = Theme.Success;
                _saveBtn.Enabled = true;
            }
        }
        catch (Exception ex)
        {
            _testStatus.Text = $"✗ {ex.Message}";
            _testStatus.ForeColor = Theme.Error;
        }
        finally
        {
            _testBtn.Enabled = true;
        }
    }

    private void OnSave(object? sender, EventArgs e)
    {
        try
        {
            ConfigStore.Save(new ConfigStore.AgentSettings(
                _urlBox.Text.Trim().TrimEnd('/'),
                _apiKeyBox.Text.Trim(),
                _azureBox.Text.Trim()));
        }
        catch (Exception ex)
        {
            MessageBox.Show($"Failed to save configuration:\n{ex.Message}",
                "Error", MessageBoxButtons.OK, MessageBoxIcon.Error);
            return;
        }

        try
        {
            ServiceManager.Start();
            ServiceStarted = true;
        }
        catch (Exception ex)
        {
            MessageBox.Show(
                $"Configuration saved, but could not start the service:\n{ex.Message}\n\n" +
                "You can start it manually via services.msc.",
                "Warning", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }

        Close();
    }
}
