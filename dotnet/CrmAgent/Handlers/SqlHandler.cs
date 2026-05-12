using System.Data.Common;
using CrmAgent.Models;
using CrmAgent.Services;
using Microsoft.Data.SqlClient;

namespace CrmAgent.Handlers;

/// <summary>
/// Executes SQL extraction jobs against MSSQL using streaming database cursors.
/// Always uses Windows Integrated Security (the service account).
/// </summary>
public sealed class SqlHandler : IJobHandler
{
    private static readonly HashSet<string> AllowedFirstTokens = ["SELECT", "WITH"];
    private const int PreviewRowLimit = 100;

    private readonly BlobStorageService _blob;
    private readonly AgentConfig _agentConfig;
    private readonly ILogger<SqlHandler> _logger;

    public SqlHandler(BlobStorageService blob, AgentConfig agentConfig, ILogger<SqlHandler> logger)
    {
        _blob = blob;
        _agentConfig = agentConfig;
        _logger = logger;
    }

    public async Task<HandlerResult> ExecuteAsync(Job job, Action<JobProgress> onProgress, CancellationToken ct)
    {
        var config = job.Config.ToSqlConfig(job);
        var connectionString = BuildMssqlConnectionString(config, _agentConfig.SqlTrustServerCertificate);

        // Guard: reject queries that aren't SELECT statements.
        var firstToken = config.Query.TrimStart().Split(' ', 2, StringSplitOptions.RemoveEmptyEntries)[0].ToUpperInvariant();
        if (!AllowedFirstTokens.Contains(firstToken))
        {
            throw new InvalidOperationException(
                $"SQL query must be a SELECT statement (got \"{firstToken}...\"). " +
                "The agent does not execute DML/DDL queries.");
        }

        // Preview mode: run a limited query and return rows inline (no blob).
        if (job.Preview)
        {
            var previewQuery = WrapQueryForPreview(config.Query);
            _logger.LogInformation("Starting SQL preview for job {JobId} (limit={Limit})", job.Id, PreviewRowLimit);

            var rows = await ExecutePreviewAsync(connectionString, previewQuery, ct);

            _logger.LogInformation("SQL preview complete for job {JobId}: {Rows} rows", job.Id, rows.Count);
            return new HandlerResult { BlobName = null, ProcessedRows = rows.Count, PreviewRows = rows };
        }

        var timestamp = DateTime.UtcNow;
        var blobName = BlobStorageService.BuildBlobName(config.BlobPath, timestamp);

        _logger.LogInformation("Starting SQL extraction for job {JobId} blob={BlobName}",
            job.Id, blobName);

        int processedRows;
        try
        {
            await using var blobStream = await _blob.OpenWriteStreamAsync(blobName, ct);
            await using (var writer = new NdjsonGzipWriter(blobStream, leaveOpen: true))
            {
                processedRows = await ExecuteMssqlAsync(connectionString, config.Query, config.HashFields, writer, onProgress, ct);
            }
        }
        catch
        {
            // Clean up partial/corrupt blob on failure
            try { await _blob.DeleteBlobIfExistsAsync(blobName, CancellationToken.None); }
            catch { /* best-effort cleanup */ }
            throw;
        }

        _logger.LogInformation("SQL extraction complete for job {JobId}: {Rows} rows → {BlobName}",
            job.Id, processedRows, blobName);

        return new HandlerResult { BlobName = blobName, ProcessedRows = processedRows };
    }

    /// <summary>
    /// Builds an MSSQL connection string from the server and database name
    /// provided by the portal, using Windows Integrated Security (the service account).
    /// SQL authentication credentials are never accepted.
    /// </summary>
    private static string BuildMssqlConnectionString(SqlJobConfig config, bool trustServerCertificate)
    {
        if (string.IsNullOrEmpty(config.Server))
            throw new InvalidOperationException("MSSQL job config missing 'server'");
        if (string.IsNullOrEmpty(config.Database))
            throw new InvalidOperationException("MSSQL job config missing 'database'");

        var builder = new SqlConnectionStringBuilder
        {
            DataSource = config.Server,
            InitialCatalog = config.Database,
            IntegratedSecurity = true,
            TrustServerCertificate = trustServerCertificate,
        };
        return builder.ConnectionString;
    }

    private static async Task<int> ExecuteMssqlAsync(
        string connectionString, string query, string[] hashFields,
        NdjsonGzipWriter writer, Action<JobProgress> onProgress, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(query, connection);
        command.CommandTimeout = 0; // No timeout — rely on CancellationToken for cancellation
        await using var reader = await command.ExecuteReaderAsync(ct);

        return await StreamReaderAsync(reader, hashFields, writer, onProgress, ct);
    }

    /// <summary>
    /// Generic streaming reader that works with any ADO.NET DbDataReader.
    /// Reads rows one at a time and writes them as NDJSON with a row hash.
    /// </summary>
    private static async Task<int> StreamReaderAsync(
        DbDataReader reader, string[] hashFields,
        NdjsonGzipWriter writer, Action<JobProgress> onProgress, CancellationToken ct)
    {
        var processedRows = 0;
        var fieldNames = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetName)
            .ToArray();

        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(fieldNames.Length);
            for (var i = 0; i < fieldNames.Length; i++)
            {
                row[fieldNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }

            row["_rowHash"] = HashService.ComputeRowHash(row, hashFields);
            await writer.WriteRowAsync(row);

            processedRows++;
            if (processedRows % 1000 == 0)
            {
                onProgress(new JobProgress
                {
                    ProcessedRows = processedRows,
                    Message = $"Processing row {processedRows}...",
                });
            }
        }

        return processedRows;
    }

    /// <summary>
    /// Wraps a SQL query with TOP to limit results for preview mode.
    /// Injects TOP N directly after the outermost SELECT keyword (or after the
    /// final SELECT in a CTE). Trailing semicolons are stripped first so that
    /// the resulting query is always valid, and ORDER BY in the original query
    /// is preserved (no subquery wrapping).
    /// </summary>
    private static string WrapQueryForPreview(string query)
    {
        // Strip trailing whitespace and semicolons before any manipulation.
        var trimmed = query.TrimStart().TrimEnd(';', ' ', '\t', '\r', '\n');

        if (trimmed.StartsWith("WITH", StringComparison.OrdinalIgnoreCase))
        {
            // Find the last SELECT at parenthesis depth 0 (the outer query after
            // the CTE). Using LastIndexOf("SELECT") would incorrectly match
            // subqueries like "WHERE id IN (SELECT ...)".
            var depth = 0;
            var lastTopLevelSelect = -1;
            for (var i = 0; i <= trimmed.Length - 6; i++)
            {
                var c = trimmed[i];
                if (c == '(') { depth++; continue; }
                if (c == ')') { depth--; continue; }

                if (depth == 0 &&
                    trimmed.AsSpan(i, 6).Equals("SELECT".AsSpan(), StringComparison.OrdinalIgnoreCase) &&
                    (i == 0 || !char.IsLetterOrDigit(trimmed[i - 1])) &&
                    (i + 6 >= trimmed.Length || !char.IsLetterOrDigit(trimmed[i + 6])))
                {
                    lastTopLevelSelect = i;
                }
            }

            if (lastTopLevelSelect >= 0)
            {
                var insertPos = lastTopLevelSelect + "SELECT".Length;
                return string.Concat(trimmed.AsSpan(0, insertPos), $" TOP {PreviewRowLimit}", trimmed.AsSpan(insertPos));
            }
        }

        // For plain SELECT queries inject TOP N directly after SELECT.
        // This preserves any ORDER BY clause and avoids the derived-table restriction
        // that would make ORDER BY invalid inside a subquery.
        if (trimmed.StartsWith("SELECT", StringComparison.OrdinalIgnoreCase))
        {
            var insertPos = "SELECT".Length;
            return string.Concat(trimmed.AsSpan(0, insertPos), $" TOP {PreviewRowLimit}", trimmed.AsSpan(insertPos));
        }

        // Fallback — should not be reached given the AllowedFirstTokens guard.
        return $"SELECT TOP {PreviewRowLimit} * FROM ({trimmed}) AS _p";
    }

    private static async Task<List<Dictionary<string, object?>>> ExecutePreviewAsync(
        string connectionString, string query, CancellationToken ct)
    {
        await using var connection = new SqlConnection(connectionString);
        await connection.OpenAsync(ct);

        await using var command = new SqlCommand(query, connection);
        command.CommandTimeout = 0; // No timeout — rely on CancellationToken for cancellation
        await using var reader = await command.ExecuteReaderAsync(ct);

        var rows = new List<Dictionary<string, object?>>();
        var fieldNames = Enumerable.Range(0, reader.FieldCount)
            .Select(reader.GetName)
            .ToArray();

        while (await reader.ReadAsync(ct))
        {
            var row = new Dictionary<string, object?>(fieldNames.Length);
            for (var i = 0; i < fieldNames.Length; i++)
            {
                row[fieldNames[i]] = reader.IsDBNull(i) ? null : reader.GetValue(i);
            }
            rows.Add(row);
        }

        return rows;
    }
}
