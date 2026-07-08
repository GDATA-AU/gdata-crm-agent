using CrmAgent.Services;

namespace CrmAgent.Tests.Fakes;

/// <summary>
/// In-memory <see cref="IBlobStorage"/> for handler tests. Each opened write
/// stream is a <see cref="MemoryStream"/> retained by blob name so the written
/// bytes can be inspected after the handler disposes it (MemoryStream.ToArray()
/// works after Dispose). Deletes are recorded rather than performed.
/// </summary>
public sealed class FakeBlobStorage : IBlobStorage
{
    public Dictionary<string, MemoryStream> Blobs { get; } = new();
    public List<string> DeletedBlobs { get; } = new();

    public Task<Stream> OpenWriteStreamAsync(string blobName, CancellationToken ct = default)
    {
        var ms = new MemoryStream();
        Blobs[blobName] = ms;
        return Task.FromResult<Stream>(ms);
    }

    public Task DeleteBlobIfExistsAsync(string blobName, CancellationToken ct = default)
    {
        DeletedBlobs.Add(blobName);
        return Task.CompletedTask;
    }
}
