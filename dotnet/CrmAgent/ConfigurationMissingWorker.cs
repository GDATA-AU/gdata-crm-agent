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
            "Agent is not configured yet. Open the tray app and save Portal URL, API key, and Azure Storage connection string, then restart the service.");

        while (!stoppingToken.IsCancellationRequested)
        {
            await Task.Delay(TimeSpan.FromMinutes(1), stoppingToken);
        }
    }
}
