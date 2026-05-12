using System.ComponentModel;
using System.Diagnostics;
using System.ServiceProcess;

namespace CrmAgent.Tray;

/// <summary>
/// Thin wrapper around <see cref="ServiceController"/> for querying and
/// controlling the crm-agent Windows service.
/// Start/Stop fall back to an elevated <c>sc.exe</c> process when the
/// tray app is running without admin rights.
/// </summary>
public static class ServiceManager
{
    public const string ServiceName = "gdata-agent";

    public static ServiceControllerStatus GetStatus()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            return sc.Status;
        }
        catch (InvalidOperationException)
        {
            // Service not installed or SCM unreachable.
            return ServiceControllerStatus.Stopped;
        }
    }

    public static void Start()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status is ServiceControllerStatus.Stopped or ServiceControllerStatus.Paused)
            {
                sc.Start();
                sc.WaitForStatus(ServiceControllerStatus.Running, TimeSpan.FromSeconds(30));
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5 /* ACCESS_DENIED */)
        {
            RunElevated("sc.exe", $"start {ServiceName}");
        }
        catch (InvalidOperationException) when (IsAccessDenied())
        {
            RunElevated("sc.exe", $"start {ServiceName}");
        }
    }

    public static void Stop()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            if (sc.Status == ServiceControllerStatus.Running)
            {
                sc.Stop();
                sc.WaitForStatus(ServiceControllerStatus.Stopped, TimeSpan.FromSeconds(30));
            }
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 5 /* ACCESS_DENIED */)
        {
            RunElevated("sc.exe", $"stop {ServiceName}");
        }
        catch (InvalidOperationException) when (IsAccessDenied())
        {
            RunElevated("sc.exe", $"stop {ServiceName}");
        }
    }

    public static void Restart()
    {
        Stop();
        Start();
    }

    /// <summary>
    /// Returns true when the current user lacks permission to open the service handle.
    /// Used as a filter for <see cref="InvalidOperationException"/> thrown by
    /// <see cref="ServiceController.Status"/> before Start/Stop is even attempted.
    /// </summary>
    private static bool IsAccessDenied()
    {
        try
        {
            using var sc = new ServiceController(ServiceName);
            _ = sc.Status;
            return false;
        }
        catch { return true; }
    }

    /// <summary>
    /// Finds and launches the registered uninstaller for GDATA CRM Agent.
    /// Returns true if the uninstaller was found and started.
    /// </summary>
    public static bool LaunchUninstaller()
    {
        var uninstallString = FindUninstallString();
        if (uninstallString is null)
            return false;

        try
        {
            // Inno Setup: "C:\...\unins000.exe"
            // MSI:        MsiExec.exe /I{ProductCode}  (we change /I to /X)
            if (uninstallString.Contains("MsiExec", StringComparison.OrdinalIgnoreCase))
            {
                var args = uninstallString
                    .Substring(uninstallString.IndexOf(' ') + 1)
                    .Replace("/I", "/X", StringComparison.OrdinalIgnoreCase);
                Process.Start(new ProcessStartInfo("msiexec.exe", args)
                {
                    Verb = "runas",
                    UseShellExecute = true,
                });
            }
            else
            {
                // Inno Setup uninstaller — strip surrounding quotes if present
                var exe = uninstallString.Trim('"');
                Process.Start(new ProcessStartInfo(exe)
                {
                    Verb = "runas",
                    UseShellExecute = true,
                });
            }
            return true;
        }
        catch { return false; }
    }

    /// <summary>
    /// Searches the Windows Uninstall registry for the GDATA CRM Agent entry
    /// and returns the UninstallString value, or null if not found.
    /// </summary>
    private static string? FindUninstallString()
    {
        // Inno Setup registers under {AppId}_is1
        string[] subKeys =
        [
            @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\{A3F6B2D1-4C8E-4F2A-9D3B-7E1C5A0F8B2D}_is1",
        ];

        foreach (var subKey in subKeys)
        {
            using var key = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(subKey);
            if (key?.GetValue("UninstallString") is string value && !string.IsNullOrWhiteSpace(value))
                return value;
        }

        // Fallback: scan all uninstall entries for one matching the display name
        const string uninstallRoot = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall";
        using var root = Microsoft.Win32.Registry.LocalMachine.OpenSubKey(uninstallRoot);
        if (root is null) return null;

        foreach (var name in root.GetSubKeyNames())
        {
            using var key = root.OpenSubKey(name);
            if (key?.GetValue("DisplayName") is string displayName &&
                displayName.Contains("GDATA CRM Agent", StringComparison.OrdinalIgnoreCase) &&
                key.GetValue("UninstallString") is string us)
            {
                return us;
            }
        }

        return null;
    }

    private static void RunElevated(string fileName, string arguments)
    {
        using var p = Process.Start(new ProcessStartInfo(fileName, arguments)
        {
            Verb = "runas",
            UseShellExecute = true,
            WindowStyle = ProcessWindowStyle.Hidden,
        });
        p?.WaitForExit(30_000);
    }
}
