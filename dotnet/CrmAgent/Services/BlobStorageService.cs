using Azure.Storage.Blobs;
using Azure.Storage.Blobs.Models;

namespace CrmAgent.Services;

/// <summary>
/// Helpers for uploading to the <c>erp-imports</c> Azure Blob Storage container.
/// </summary>
public sealed class BlobStorageService
{
    private const string ContainerName = "erp-imports";

    private readonly BlobContainerClient _container;

    public BlobStorageService(AgentConfig config)
    {
        var serviceClient = new BlobServiceClient(config.AzureStorageConnectionString);
        _container = serviceClient.GetBlobContainerClient(ContainerName);
    }

    /// <summary>
    /// Upload a stream to blob storage.
    /// </summary>
    public async Task UploadStreamAsync(string blobName, Stream stream, CancellationToken ct = default)
    {
        var blobClient = _container.GetBlobClient(blobName);
        await blobClient.UploadAsync(stream, new BlobUploadOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/gzip" },
        }, cancellationToken: ct);
    }

    /// <summary>
    /// Open a write stream directly to blob storage. Data written to the
    /// returned stream is uploaded progressively, avoiding the need to buffer
    /// the entire payload in memory.
    /// </summary>
    public async Task<Stream> OpenWriteStreamAsync(string blobName, CancellationToken ct = default)
    {
        var blobClient = _container.GetBlobClient(blobName);
        return await blobClient.OpenWriteAsync(overwrite: true, new BlobOpenWriteOptions
        {
            HttpHeaders = new BlobHttpHeaders { ContentType = "application/gzip" },
        }, cancellationToken: ct);
    }

    /// <summary>
    /// Delete a blob if it exists. Used to clean up partial/corrupt blobs
    /// when extraction fails mid-stream.
    /// </summary>
    public async Task DeleteBlobIfExistsAsync(string blobName, CancellationToken ct = default)
    {
        var blobClient = _container.GetBlobClient(blobName);
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
