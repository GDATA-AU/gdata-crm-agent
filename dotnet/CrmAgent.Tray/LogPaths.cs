using System.Diagnostics;

namespace CrmAgent.Tray;

/// <summary>
/// Single source for the agent's log folder location and the "open it in the
/// file browser" action, shared by the tray menu, the status form, and the tailer.
/// </summary>
internal static class LogPaths
{
    public static readonly string LogDirectory = Path.Combine(ConfigStore.ConfigDirectory, "logs");

    public static void Open()
    {
        Directory.CreateDirectory(LogDirectory);
        Process.Start(new ProcessStartInfo { FileName = LogDirectory, UseShellExecute = true });
    }
}
