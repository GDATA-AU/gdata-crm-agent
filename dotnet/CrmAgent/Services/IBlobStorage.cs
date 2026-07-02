namespace CrmAgent.Services;

/// <summary>
/// Abstraction over the blob-storage operations the job handlers depend on,
/// so they can be unit-tested without a live Azure Blob Storage account.
/// </summary>
public interface IBlobStorage
{
    Task<Stream> OpenWriteStreamAsync(string blobName, CancellationToken ct = default);
    Task DeleteBlobIfExistsAsync(string blobName, CancellationToken ct = default);
}
