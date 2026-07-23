using Azure;
using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace CrmAgent.Services;

/// <summary>
/// Helpers for uploading to the <c>erp-imports</c> Azure Blob Storage container.
/// </summary>
public sealed class BlobStorageService : IBlobStorage
{
    private const string ContainerName = "erp-imports";

    /// <summary>
    /// Actionable error surfaced (to the agent log and the portal job status) when the
    /// target container is missing. Deliberately names the container and the setting to
    /// check, since the raw Azure error ("The specified container does not exist.") gives
    /// no indication of which container or storage account is at fault.
    /// </summary>
    internal static string ContainerNotFoundMessage =>
        $"Azure Blob container '{ContainerName}' was not found in the configured storage account. " +
        "The agent does not create it — the container must already exist in the storage account " +
        "that Agent:AzureStorageConnectionString points to. Verify the connection string targets " +
        $"the correct account and that the '{ContainerName}' container has been created.";

    /// <summary>
    /// True when <paramref name="ex"/> is Azure's 404/ContainerNotFound response, as opposed
    /// to a missing blob or any other request failure.
    /// </summary>
    internal static bool IsContainerNotFound(RequestFailedException ex)
        => ex.Status == 404
           && string.Equals(ex.ErrorCode, "ContainerNotFound", StringComparison.OrdinalIgnoreCase);

    private readonly Lazy<BlobContainerClient> _container;

    public BlobStorageService(AgentConfig config)
    {
        _container = new Lazy<BlobContainerClient>(() =>
        {
            var serviceClient = new BlobServiceClient(config.AzureStorageConnectionString);
            return serviceClient.GetBlobContainerClient(ContainerName);
        });
    }

    /// <summary>
    /// Open a write stream directly to blob storage. Data written to the
    /// returned stream is uploaded progressively, avoiding the need to buffer
    /// the entire payload in memory.
    /// </summary>
    public async Task<Stream> OpenWriteStreamAsync(string blobName, CancellationToken ct = default)
    {
        var blobClient = _container.Value.GetBlobClient(blobName);
        try
        {
            return await blobClient.OpenWriteAsync(overwrite: true, new BlobOpenWriteOptions
            {
                HttpHeaders = new BlobHttpHeaders { ContentType = "application/gzip" },
            }, cancellationToken: ct);
        }
        catch (RequestFailedException ex) when (IsContainerNotFound(ex))
        {
            // Translate the opaque Azure 404 ("The specified container does not exist.")
            // into an actionable message. The agent deliberately does not create the
            // container — that is the storage owner's responsibility — so surface exactly
            // what is missing and where to fix it.
            throw new InvalidOperationException(ContainerNotFoundMessage, ex);
        }
    }

    /// <summary>
    /// Delete a blob if it exists. Used to clean up partial/corrupt blobs
    /// when extraction fails mid-stream.
    /// </summary>
    public async Task DeleteBlobIfExistsAsync(string blobName, CancellationToken ct = default)
    {
        var blobClient = _container.Value.GetBlobClient(blobName);
        await blobClient.DeleteIfExistsAsync(cancellationToken: ct);
    }

    /// <summary>
    /// Build the full blob name from a path prefix and a timestamp.
    /// Throws <see cref="InvalidOperationException"/> if <paramref name="blobPath"/>
    /// contains <c>..</c> segments or starts with <c>/</c>, which would allow a
    /// malicious portal to escape the expected path hierarchy.
    /// </summary>
    public static string BuildBlobName(string blobPath, DateTime timestamp)
    {
        if (string.IsNullOrEmpty(blobPath))
            throw new InvalidOperationException("BlobPath must not be empty.");

        // Normalise to forward-slash separators first, then validate.
        var normalised = blobPath.Replace('\\', '/');

        if (normalised.StartsWith('/'))
            throw new InvalidOperationException($"BlobPath must not start with '/': '{blobPath}'");

        // URL-decode each segment so that %2e%2e and similar encodings are caught too.
        foreach (var segment in normalised.Split('/'))
        {
            string decoded;
            try
            {
                decoded = Uri.UnescapeDataString(segment);
            }
            catch (UriFormatException)
            {
                throw new InvalidOperationException($"BlobPath contains invalid percent-encoding: '{blobPath}'");
            }
            if (decoded == "..")
                throw new InvalidOperationException($"BlobPath must not contain '..' segments: '{blobPath}'");
        }

        var ts = timestamp
            .ToUniversalTime()
            .ToString("yyyy-MM-ddTHH-mm-ssZ");
        var prefix = normalised.EndsWith('/') ? normalised : normalised + "/";
        return $"{prefix}{ts}.ndjson.gz";
    }
}
