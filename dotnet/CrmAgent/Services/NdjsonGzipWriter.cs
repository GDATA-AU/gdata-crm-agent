using System.IO.Compression;
using System.Text.Json;

namespace CrmAgent.Services;

/// <summary>
/// Writes NDJSON rows through a GZip stream.  The compressed output is
/// written to the underlying <see cref="Stream"/> passed at construction.
/// </summary>
public sealed class NdjsonGzipWriter : IAsyncDisposable
{
    private static readonly byte[] Newline = "\n"u8.ToArray();
    private readonly GZipStream _gzip;
    private bool _disposed;

    public NdjsonGzipWriter(Stream output, bool leaveOpen = false)
    {
        _gzip = new GZipStream(output, CompressionLevel.Optimal, leaveOpen: leaveOpen);
    }

    /// <summary>
    /// Write a single row as a JSON line.
    /// Serialises directly to UTF-8 bytes to avoid intermediate string allocations.
    /// </summary>
    public async Task WriteRowAsync(Dictionary<string, object?> row, CancellationToken ct = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        await _gzip.WriteAsync(JsonSerializer.SerializeToUtf8Bytes(row), ct);
        await _gzip.WriteAsync(Newline, ct);
    }

    /// <summary>
    /// Flush and close the underlying GZip stream.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;
        await _gzip.DisposeAsync();
    }
}
