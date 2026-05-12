using CrmAgent.Tray;

// Prevent multiple tray instances using a named mutex.
bool createdNew;
using var mutex = new Mutex(true, "Global\\GDATAAgentTray_4F8A2C1D", out createdNew);
if (!createdNew)
    return;

// Check for updated-from sentinel file written by the update watchdog script.
// We use a file because explorer.exe (used to de-elevate) doesn't forward args.
string? updatedFromVersion = null;
var sentinelPath = Path.Combine(Path.GetTempPath(), "GDATAAgent-updated-from.txt");
if (File.Exists(sentinelPath))
{
    try
    {
        updatedFromVersion = File.ReadAllText(sentinelPath).Trim();
        File.Delete(sentinelPath);
    }
    catch { /* best effort */ }
}

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.Run(new TrayApplicationContext(updatedFromVersion));
