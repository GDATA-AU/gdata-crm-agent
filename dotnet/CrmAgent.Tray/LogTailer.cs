using System.Text.Json;
using System.Text.Json.Nodes;

namespace CrmAgent.Tray;

/// <summary>
/// Tails the agent's structured JSON log file and yields new entries.
/// The agent writes Serilog JSON lines to %ProgramData%\GDATA CRM Agent\logs\agent*.log.
/// </summary>
public sealed class LogTailer
{
    private static readonly string LogDirectory = Path.Combine(
        ConfigStore.ConfigDirectory, "logs");

    private long _lastPosition;
    private string? _lastFile;

    public sealed record LogEntry(DateTime Timestamp, string Level, string Message);

    /// <summary>
    /// Returns new log entries since the last call.
    /// On first call, returns the last <paramref name="initialTail"/> entries.
    /// </summary>
    public List<LogEntry> ReadNewEntries(int initialTail = 50)
    {
        var entries = new List<LogEntry>();
        var logFile = FindLatestLogFile();
        if (logFile is null) return entries;

        // If the file changed (new day roll), reset position.
        if (logFile != _lastFile)
        {
            _lastPosition = 0;
            _lastFile = logFile;
        }

        try
        {
            using var fs = new FileStream(logFile, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);

            if (_lastPosition == 0)
            {
                // First read: grab the tail of the file.
                var allEntries = ReadAllEntries(fs);
                entries.AddRange(allEntries.Count > initialTail
                    ? allEntries.GetRange(allEntries.Count - initialTail, initialTail)
                    : allEntries);
                _lastPosition = fs.Length;
            }
            else
            {
                if (fs.Length < _lastPosition)
                    _lastPosition = 0; // File was truncated/recreated.

                fs.Seek(_lastPosition, SeekOrigin.Begin);
                using var reader = new StreamReader(fs, leaveOpen: true);
                while (reader.ReadLine() is { } line)
                {
                    if (TryParseLine(line) is { } entry)
                        entries.Add(entry);
                }
                _lastPosition = fs.Position;
            }
        }
        catch (IOException)
        {
            // File may be locked momentarily; skip this cycle.
        }

        return entries;
    }

    private static List<LogEntry> ReadAllEntries(FileStream fs)
    {
        var entries = new List<LogEntry>();
        using var reader = new StreamReader(fs, leaveOpen: true);
        while (reader.ReadLine() is { } line)
        {
            if (TryParseLine(line) is { } entry)
                entries.Add(entry);
        }
        return entries;
    }

    private static LogEntry? TryParseLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line)) return null;
        try
        {
            var node = JsonNode.Parse(line);
            if (node is null) return null;

            var timestamp = node["Timestamp"]?.GetValue<DateTime>() ?? DateTime.Now;
            var level = node["Level"]?.GetValue<string>() ?? "Information";
            var message = node["RenderedMessage"]?.GetValue<string>()
                       ?? node["MessageTemplate"]?.GetValue<string>()
                       ?? line;

            // Append the exception message (if any) so failures aren't opaque.
            var exception = node["Exception"]?.GetValue<string>();
            if (exception is not null)
            {
                // Show just the first line (the exception type + message),
                // not the full stack trace, to keep the tray UI readable.
                var firstLine = exception.AsSpan();
                var newlineIdx = firstLine.IndexOfAny('\r', '\n');
                if (newlineIdx >= 0)
                    firstLine = firstLine[..newlineIdx];
                message = $"{message} — {firstLine}";
            }

            return new LogEntry(timestamp, level, message);
        }
        catch
        {
            return null;
        }
    }

    private static string? FindLatestLogFile()
    {
        if (!Directory.Exists(LogDirectory)) return null;

        return Directory.GetFiles(LogDirectory, "agent*.log")
            .OrderByDescending(File.GetLastWriteTimeUtc)
            .FirstOrDefault();
    }
}
