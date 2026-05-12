using CrmAgent;
using CrmAgent.Handlers;
using CrmAgent.Services;
using Serilog;

// ---------------------------------------------------------------------------
// Configure Serilog for structured JSON logging (matches the Node.js pino output)
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

    if (hasRequiredConfig)
    {
        var agentConfig = new AgentConfig
        {
            PortalUrl = portalUrl!.TrimEnd('/'),
            AgentApiKey = apiKey!,
            AzureStorageConnectionString = storageConnectionString!,
            PollIntervalMs = int.TryParse(
                builder.Configuration["Agent:PollIntervalMs"] ?? Environment.GetEnvironmentVariable("POLL_INTERVAL_MS"),
                out var poll) ? poll : 30_000,
            HeartbeatIntervalMs = int.TryParse(
                builder.Configuration["Agent:HeartbeatIntervalMs"] ?? Environment.GetEnvironmentVariable("HEARTBEAT_INTERVAL_MS"),
                out var hb) ? hb : 30_000,
            SqlTrustServerCertificate = bool.TryParse(
                builder.Configuration["Agent:SqlTrustServerCertificate"] ?? Environment.GetEnvironmentVariable("SQL_TRUST_SERVER_CERTIFICATE"),
                out var trustCert) ? trustCert : true,
            RestApiTimeoutSeconds = int.TryParse(
                builder.Configuration["Agent:RestApiTimeoutSeconds"] ?? Environment.GetEnvironmentVariable("REST_API_TIMEOUT_SECONDS"),
                out var apiTimeout) && apiTimeout >= 1 ? apiTimeout : 300,
        };

        builder.Services.AddSingleton(agentConfig);

        // Portal HTTP client — base address + auth header configured once.
        builder.Services.AddHttpClient<PortalClient>(http =>
        {
            http.BaseAddress = new Uri(agentConfig.PortalUrl);
            http.DefaultRequestHeaders.Add("Authorization", $"Bearer {agentConfig.AgentApiKey}");
        });

        // Generic HTTP client factory for REST API handler (outbound API calls).
        builder.Services.AddHttpClient();

        // Services
        builder.Services.AddSingleton<BlobStorageService>();
        builder.Services.AddTransient<SqlHandler>();
        builder.Services.AddTransient<RestApiHandler>();
        builder.Services.AddSingleton<HandlerFactory>();

        // Worker
        builder.Services.AddHostedService<AgentWorker>();
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
