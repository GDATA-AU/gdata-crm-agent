using System.Diagnostics;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json.Serialization;
using System.Windows.Forms;

namespace CrmAgent.Tray;

/// <summary>
/// Periodically checks GitHub Releases for a newer MSI and downloads it.
/// Raises <see cref="UpdateReady"/> when a new version has been downloaded
/// and is ready to install.
/// </summary>
public sealed class UpdateService : IDisposable
{
    private const string GitHubOwner = "GDATA-AU";
    private const string GitHubRepo = "gdata-crm-agent";
    private const string ReleaseUrl = $"https://api.github.com/repos/{GitHubOwner}/{GitHubRepo}/releases/latest";
    private const int CheckDelayMs = 30_000;         // first check 30s after startup
    private const int CheckIntervalMs = 4 * 60 * 60 * 1000; // then every 4 hours

    private readonly System.Windows.Forms.Timer _timer;
    private readonly HttpClient _http;
    private volatile bool _checking;

    /// <summary>Fired on the UI thread when a new MSI has been downloaded and is ready to install.</summary>
    public event Action<string, string>? UpdateReady;

    /// <summary>Fired on the UI thread during MSI download with (bytesReceived, totalBytes). totalBytes is -1 if unknown.</summary>
    public event Action<long, long>? DownloadProgress;

    /// <summary>Fired after the update watchdog script has been launched successfully. Subscribers should close UI.</summary>
    public event Action? ApplyingUpdate;

    /// <summary>Fired on the UI thread when an MSI download fails mid-stream. Subscribers should reset download UI.</summary>
    public event Action? DownloadFailed;

    /// <summary>The latest version available on GitHub, or null if not yet checked / up-to-date.</summary>
    public string? AvailableVersion { get; private set; }

    /// <summary>Local path to the downloaded MSI, or null if no update is pending.</summary>
    public string? DownloadedMsiPath { get; private set; }

    public UpdateService()
    {
        _http = new HttpClient();
        _http.DefaultRequestHeaders.UserAgent.ParseAdd($"GDATAAgentTray/{CurrentVersion}");
        _http.Timeout = TimeSpan.FromSeconds(30);

        _timer = new System.Windows.Forms.Timer();
        _timer.Tick += async (_, _) => await CheckForUpdateAsync();
    }

    /// <summary>Start the background check timer. First check runs after 30 seconds.</summary>
    public void Start()
    {
        _timer.Interval = CheckDelayMs;
        _timer.Start();
    }

    /// <summary>Manually trigger an update check (e.g. from a "Check for Updates" button).</summary>
    public async Task CheckNowAsync()
    {
        await CheckForUpdateAsync();
    }

    /// <summary>Launch a watchdog script that runs the MSI and relaunches the tray.</summary>
    public void ApplyUpdate(NotifyIcon notifyIcon)
    {
        if (DownloadedMsiPath is null || !File.Exists(DownloadedMsiPath))
            return;

        var trayExePath = Environment.ProcessPath;
        if (trayExePath is null) return;

        var version = AvailableVersion ?? "unknown";

        // Build a temp batch script that:
        //  1. Runs the MSI silently
        //  2. Relaunches the tray via explorer.exe (de-elevated) with --updated-from on success
        //  3. Self-deletes
        var scriptPath = Path.Combine(Path.GetTempPath(), $"GDATAAgentUpdate-{version}.cmd");
        var scriptContent = $"""
            @echo off
            msiexec /qn /i "{DownloadedMsiPath}"
            if %ERRORLEVEL% EQU 0 (
                explorer.exe "{trayExePath}" --updated-from {version}
            ) else (
                explorer.exe "{trayExePath}"
            )
            del "%~f0"
            """;
        File.WriteAllText(scriptPath, scriptContent);

        // Show "Installing update…" balloon
        notifyIcon.BalloonTipTitle = "Installing Update";
        notifyIcon.BalloonTipText = $"Updating to GDATA CRM Agent {version}…";
        notifyIcon.ShowBalloonTip(5_000);

        // Brief pause so the toast is visible before UAC prompt
        Task.Delay(500).ContinueWith(_ =>
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "cmd.exe",
                    Arguments = $"/c \"{scriptPath}\"",
                    Verb = "runas",
                    UseShellExecute = true,
                    WindowStyle = ProcessWindowStyle.Hidden,
                });

                // Process launched successfully — now close the UI
                ApplyingUpdate?.Invoke();
            }
            catch (System.ComponentModel.Win32Exception)
            {
                // User declined UAC prompt — let them know
                notifyIcon.BalloonTipTitle = "Update Cancelled";
                notifyIcon.BalloonTipText = "The update was cancelled. You can try again from the status window.";
                notifyIcon.ShowBalloonTip(5_000);
            }
        }, TaskScheduler.FromCurrentSynchronizationContext());
    }

    private async Task CheckForUpdateAsync()
    {
        if (_checking) return;
        _checking = true;

        try
        {
            // After the first check, switch to the long interval
            if (_timer.Interval == CheckDelayMs)
                _timer.Interval = CheckIntervalMs;

            var release = await _http.GetFromJsonAsync<GitHubRelease>(ReleaseUrl);
            if (release?.TagName is null) return;

            var remoteVersion = ParseVersion(release.TagName);
            if (remoteVersion is null || remoteVersion <= CurrentVersion) return;

            // Find the MSI asset
            var msiAsset = release.Assets?.FirstOrDefault(a =>
                a.BrowserDownloadUrl is not null &&
                a.Name?.EndsWith(".msi", StringComparison.OrdinalIgnoreCase) == true);
            if (msiAsset?.BrowserDownloadUrl is null) return;

            // Download to temp
            var tempPath = Path.Combine(Path.GetTempPath(), $"GDATAAgent-{release.TagName}.msi");
            if (!File.Exists(tempPath))
            {
                var partPath = tempPath + ".part";
                try
                {
                    using var response = await _http.GetAsync(msiAsset.BrowserDownloadUrl, HttpCompletionOption.ResponseHeadersRead);
                    response.EnsureSuccessStatusCode();
                    var totalBytes = response.Content.Headers.ContentLength ?? -1L;
                    await using var fs = new FileStream(partPath, FileMode.Create, FileAccess.Write, FileShare.None);
                    using var contentStream = await response.Content.ReadAsStreamAsync();
                    var buffer = new byte[81920];
                    long bytesReceived = 0;
                    int bytesRead;
                    while ((bytesRead = await contentStream.ReadAsync(buffer)) > 0)
                    {
                        await fs.WriteAsync(buffer.AsMemory(0, bytesRead));
                        bytesReceived += bytesRead;
                        DownloadProgress?.Invoke(bytesReceived, totalBytes);
                    }
                }
                catch
                {
                    try { File.Delete(partPath); } catch { /* best effort cleanup */ }
                    DownloadFailed?.Invoke();
                    return;
                }

                File.Move(partPath, tempPath);
            }

            AvailableVersion = release.TagName;
            DownloadedMsiPath = tempPath;
            UpdateReady?.Invoke(release.TagName, tempPath);
        }
        catch
        {
            // Network errors, rate limits, etc. — silently ignore and retry next cycle.
        }
        finally
        {
            _checking = false;
        }
    }

    private static Version? ParseVersion(string tag)
    {
        var cleaned = tag.TrimStart('v', 'V');
        return Version.TryParse(cleaned, out var v) ? v : null;
    }

    internal static Version CurrentVersion =>
        Assembly.GetEntryAssembly()?.GetName().Version ?? new Version(0, 0, 0);

    public void Dispose()
    {
        _timer.Dispose();
        _http.Dispose();
    }

    // DTOs for the GitHub API response (minimal)
    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")]
        public string? TagName { get; set; }

        [JsonPropertyName("assets")]
        public List<GitHubAsset>? Assets { get; set; }
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("browser_download_url")]
        public string? BrowserDownloadUrl { get; set; }
    }
}
