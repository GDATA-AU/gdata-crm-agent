namespace CrmAgent;

/// <summary>
/// Keeps the Windows service alive when credentials are not configured yet.
/// This avoids SCM startup timeout errors and gives operators a clear log message.
/// </summary>
public sealed class ConfigurationMissingWorker : BackgroundService
{
    private readonly ILogger<ConfigurationMissingWorker> _logger;

    public ConfigurationMissingWorker(ILogger<ConfigurationMissingWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogWarning(
            "Agent is not configured yet. Configure Portal URL, API key, and Azure Storage connection string via the tray app, environment variables (PORTAL_URL, AGENT_API_KEY, AZURE_STORAGE_CONNECTION_STRING), or ProgramData appsettings.json, then restart the service.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
