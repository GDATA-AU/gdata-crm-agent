using CrmAgent.Tray;

// Prevent multiple tray instances using a named mutex.
bool createdNew;
using var mutex = new Mutex(true, "Global\\GDATAAgentTray_4F8A2C1D", out createdNew);
if (!createdNew)
    return;

// Check for --updated-from flag (set by the update watchdog script)
string? updatedFromVersion = null;
for (int i = 0; i < args.Length - 1; i++)
{
    if (args[i] == "--updated-from")
    {
        updatedFromVersion = args[i + 1];
        break;
    }
}

Application.SetHighDpiMode(HighDpiMode.PerMonitorV2);
Application.EnableVisualStyles();
Application.SetCompatibleTextRenderingDefault(false);
Application.Run(new TrayApplicationContext(updatedFromVersion));
