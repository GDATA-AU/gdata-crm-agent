using System.Diagnostics;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace CrmAgent.Tray;

/// <summary>
/// Reads and writes the agent configuration stored in
/// %ProgramData%\GDATA CRM Agent\appsettings.json.
/// The Worker Service is configured to layer this file on top of
/// its own appsettings.json so credentials are kept out of Program Files.
/// </summary>
public static class ConfigStore
{
    private static readonly JsonSerializerOptions WriteOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    public static readonly string ConfigDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GDATA CRM Agent");

    public static readonly string ConfigPath = Path.Combine(ConfigDirectory, "appsettings.json");

    public sealed record AgentSettings(
        string PortalUrl,
        string AgentApiKey,
        string AzureStorageConnectionString);

    public static bool IsConfigured()
    {
        var s = Load();
        return s is not null
            && !string.IsNullOrWhiteSpace(s.PortalUrl)
            && !string.IsNullOrWhiteSpace(s.AgentApiKey)
            && !string.IsNullOrWhiteSpace(s.AzureStorageConnectionString);
    }

    public static AgentSettings? Load()
    {
        if (!File.Exists(ConfigPath)) return null;
        try
        {
            using var stream = File.OpenRead(ConfigPath);
            var root = JsonNode.Parse(stream);
            var agent = root?["Agent"];
            if (agent is null) return null;
            return new AgentSettings(
                agent["PortalUrl"]?.GetValue<string>() ?? "",
                agent["AgentApiKey"]?.GetValue<string>() ?? "",
                agent["AzureStorageConnectionString"]?.GetValue<string>() ?? "");
        }
        catch { return null; }
    }

    public static void Save(AgentSettings settings)
    {
        var root = new JsonObject
        {
            ["Agent"] = new JsonObject
            {
                ["PortalUrl"] = settings.PortalUrl,
                ["AgentApiKey"] = settings.AgentApiKey,
                ["AzureStorageConnectionString"] = settings.AzureStorageConnectionString,
                ["PollIntervalMs"] = 5000,
                ["HeartbeatIntervalMs"] = 5000
            }
        };
        var json = root.ToJsonString(WriteOptions);

        try
        {
            Directory.CreateDirectory(ConfigDirectory);
            File.WriteAllText(ConfigPath, json);
        }
        catch (UnauthorizedAccessException)
        {
            // The tray app runs as a standard user. If the installer hasn't set
            // ACLs yet (or the directory was recreated), fall back to a single
            // elevated process that creates the dir, sets permissions, and writes
            // the file — matching the installer's icacls behavior.
            SaveElevated(json);
        }
    }

    private static void SaveElevated(string json)
    {
        var tempFile = Path.Combine(Path.GetTempPath(), $"crmagent-{Guid.NewGuid():N}.json");
        File.WriteAllText(tempFile, json);

        try
        {
            // Single elevated cmd: create dir, grant Users group modify access
            // (S-1-5-32-545 is locale-independent), copy the file, delete temp.
            var args = $"/c mkdir \"{ConfigDirectory}\" 2>nul & " +
                       $"icacls \"{ConfigDirectory}\" /grant *S-1-5-32-545:(OI)(CI)M /T /Q & " +
                       $"copy /Y \"{tempFile}\" \"{ConfigPath}\" & " +
                       $"del \"{tempFile}\"";

            using var p = Process.Start(new ProcessStartInfo("cmd.exe", args)
            {
                Verb = "runas",
                UseShellExecute = true,
                WindowStyle = ProcessWindowStyle.Hidden,
            });
            p?.WaitForExit(30_000);

            if (!File.Exists(ConfigPath))
                throw new IOException("Elevated save did not produce the configuration file.");
        }
        finally
        {
            // Clean up temp file if it wasn't moved by the elevated process.
            try { File.Delete(tempFile); } catch { /* best effort */ }
        }
    }
}
