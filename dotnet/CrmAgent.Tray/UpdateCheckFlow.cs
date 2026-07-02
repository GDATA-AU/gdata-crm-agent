namespace CrmAgent.Tray;

/// <summary>
/// Shared implementation of the manual "check for updates" UI sequence used by both
/// the tray menu and the status form: show "Checking…", disable the control, run
/// <see cref="UpdateService.CheckNowAsync"/>; when no update is found show a placeholder
/// and reset to idle text after 3 seconds; on failure show the failure text; always
/// re-enable. The update-found path is handled separately by the UpdateReady event.
/// </summary>
internal static class UpdateCheckFlow
{
    public static async Task RunAsync(
        UpdateService svc,
        Action<string> setText,
        Action<bool> setEnabled,
        string noUpdateText,
        string failedText,
        string idleText)
    {
        setText("Checking…");
        setEnabled(false);
        try
        {
            await svc.CheckNowAsync();
            if (svc.AvailableVersion is null)
            {
                setText(noUpdateText);
                var resetTimer = new System.Windows.Forms.Timer { Interval = 3_000 };
                resetTimer.Tick += (_, _) =>
                {
                    setText(idleText);
                    resetTimer.Stop();
                    resetTimer.Dispose();
                };
                resetTimer.Start();
            }
        }
        catch
        {
            setText(failedText);
        }
        finally
        {
            setEnabled(true);
        }
    }
}
