using Azure.Storage.Blobs;
using CrmAgent;
using CrmAgent.Handlers;
using CrmAgent.Services;
using Microsoft.Extensions.Configuration;
using Serilog;

// ---------------------------------------------------------------------------
// Configure Serilog for structured JSON logging: one JSON object per line so the
// tray's log tailer can parse each entry.
// ---------------------------------------------------------------------------
Log.Logger = new LoggerConfiguration()
    .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter(renderMessage: true))
    .CreateBootstrapLogger();

try
{
    var builder = Host.CreateApplicationBuilder(args);

    // Layer credentials written by the tray app on first-run.
    // This file wins over the built-in appsettings.json so IT staff never
    // need to edit files inside Program Files.
    var programDataConfig = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GDATA CRM Agent",
        "appsettings.json");
    builder.Configuration.AddJsonFile(programDataConfig, optional: true, reloadOnChange: false);

    // Serilog
    var logDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        "GDATA CRM Agent",
        "logs");
    builder.Services.AddSerilog((services, cfg) => cfg
        .ReadFrom.Configuration(builder.Configuration)
        .ReadFrom.Services(services)
        .WriteTo.Console(new Serilog.Formatting.Json.JsonFormatter(renderMessage: true))
        .WriteTo.File(
            new Serilog.Formatting.Json.JsonFormatter(renderMessage: true),
            Path.Combine(logDirectory, "agent.log"),
            rollingInterval: RollingInterval.Day,
            retainedFileCountLimit: 7,
            shared: true,
            flushToDiskInterval: TimeSpan.FromSeconds(1)));

    // Windows Service support — no-op on Linux, enables SCM integration on Windows.
    builder.Services.AddWindowsService(options =>
    {
        options.ServiceName = "gdata-agent";
    });

    // Configuration — bind from appsettings.json "Agent" section, then overlay
    // environment variables for backwards compatibility.
    // Empty strings in config are treated the same as missing (null) so that the
    // placeholder values in appsettings.json don't shadow environment variables.
    static string? NonEmpty(string? s) => string.IsNullOrEmpty(s) ? null : s;

    var portalUrl = (NonEmpty(builder.Configuration["Agent:PortalUrl"])
        ?? NonEmpty(Environment.GetEnvironmentVariable("PORTAL_URL")))?.Trim();
    var apiKey = (NonEmpty(builder.Configuration["Agent:AgentApiKey"])
        ?? NonEmpty(Environment.GetEnvironmentVariable("AGENT_API_KEY")))?.Trim();
    var storageConnectionString = (NonEmpty(builder.Configuration["Agent:AzureStorageConnectionString"])
        ?? NonEmpty(Environment.GetEnvironmentVariable("AZURE_STORAGE_CONNECTION_STRING")))?.Trim();

    var hasRequiredConfig = !string.IsNullOrWhiteSpace(portalUrl)
        && !string.IsNullOrWhiteSpace(apiKey)
        && !string.IsNullOrWhiteSpace(storageConnectionString);

    // Validate the Azure Storage connection string format before registering services.
    // BlobServiceClient parses the connection string in its constructor, so this detects
    // malformed values (e.g. missing '=' in a segment) at startup rather than on the first job.
    if (hasRequiredConfig)
    {
        try { _ = new BlobServiceClient(storageConnectionString); }
        catch (Exception ex) when (ex is FormatException or InvalidOperationException or ArgumentException)
        {
            Log.Error(ex, "Azure Storage connection string is invalid — check Agent:AzureStorageConnectionString in appsettings.json and restart the service");
            hasRequiredConfig = false;
        }
    }

    if (hasRequiredConfig)
    {
        var agentConfig = new AgentConfig
        {
            PortalUrl = portalUrl!.TrimEnd('/'),
            AgentApiKey = apiKey!,
            AzureStorageConnectionString = storageConnectionString!,
            PollIntervalMs = GetInt(builder.Configuration, "Agent:PollIntervalMs", "POLL_INTERVAL_MS",
                AgentConfig.DefaultPollIntervalMs),
            HeartbeatIntervalMs = GetInt(builder.Configuration, "Agent:HeartbeatIntervalMs", "HEARTBEAT_INTERVAL_MS",
                AgentConfig.DefaultHeartbeatIntervalMs),
            SqlTrustServerCertificate = bool.TryParse(
                builder.Configuration["Agent:SqlTrustServerCertificate"] ?? Environment.GetEnvironmentVariable("SQL_TRUST_SERVER_CERTIFICATE"),
                out var trustCert) ? trustCert : true,
            RestApiTimeoutSeconds = GetInt(builder.Configuration, "Agent:RestApiTimeoutSeconds", "REST_API_TIMEOUT_SECONDS",
                AgentConfig.DefaultRestApiTimeoutSeconds, min: 1),
            SqlCommandTimeoutSeconds = GetInt(builder.Configuration, "Agent:SqlCommandTimeoutSeconds", "SQL_COMMAND_TIMEOUT_SECONDS",
                AgentConfig.DefaultSqlCommandTimeoutSeconds, min: 0),
            SqlConnectTimeoutSeconds = GetInt(builder.Configuration, "Agent:SqlConnectTimeoutSeconds", "SQL_CONNECT_TIMEOUT_SECONDS",
                AgentConfig.DefaultSqlConnectTimeoutSeconds, min: 1),
            MaxJobDurationSeconds = GetInt(builder.Configuration, "Agent:MaxJobDurationSeconds", "MAX_JOB_DURATION_SECONDS",
                AgentConfig.DefaultMaxJobDurationSeconds, min: 0),
            WatchdogGraceSeconds = GetInt(builder.Configuration, "Agent:WatchdogGraceSeconds", "WATCHDOG_GRACE_SECONDS",
                AgentConfig.DefaultWatchdogGraceSeconds, min: 0),
            MaxRestApiPages = GetInt(builder.Configuration, "Agent:MaxRestApiPages", "MAX_REST_API_PAGES",
                AgentConfig.DefaultMaxRestApiPages, min: 1),
        };

        builder.Services.AddSingleton(agentConfig);

        // Portal HTTP client — base address + auth header configured once.
        builder.Services.AddHttpClient<PortalClient>(http =>
        {
            http.BaseAddress = new Uri(agentConfig.PortalUrl);
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {agentConfig.AgentApiKey}");
            http.DefaultRequestHeaders.Add("X-Agent-Machine-Name", Environment.MachineName.Replace("\r", "").Replace("\n", ""));
            http.Timeout = TimeSpan.FromSeconds(30);
        });

        // Generic HTTP client factory for REST API handler (outbound API calls).
        builder.Services.AddHttpClient();

        // Services
        builder.Services.AddSingleton(TimeProvider.System);
        builder.Services.AddSingleton<AgentLiveness>();
        builder.Services.AddSingleton<BlobStorageService>();
        builder.Services.AddTransient<SqlHandler>();
        builder.Services.AddTransient<RestApiHandler>();
        builder.Services.AddSingleton<HandlerFactory>();

        // Worker + liveness watchdog (force-restarts the process if a job wedges)
        builder.Services.AddHostedService<AgentWorker>();
        builder.Services.AddHostedService<WatchdogService>();
    }
    else
    {
        builder.Services.AddHostedService<ConfigurationMissingWorker>();
    }

    var host = builder.Build();
    host.Run();
}
catch (Exception ex)
{
    Log.Fatal(ex, "Agent terminated unexpectedly");
    Environment.ExitCode = 1;
}
finally
{
    await Log.CloseAndFlushAsync();
}

static int GetInt(IConfiguration cfg, string key, string envVar, int fallback, int min = int.MinValue)
{
    var raw = cfg[key] ?? Environment.GetEnvironmentVariable(envVar);
    return int.TryParse(raw, out var v) && v >= min ? v : fallback;
}
