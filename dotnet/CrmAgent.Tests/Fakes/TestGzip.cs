using System.IO.Compression;
using System.Text;

namespace CrmAgent.Tests.Fakes;

/// <summary>Helpers for decompressing gzip NDJSON blob output written by the handlers.</summary>
public static class TestGzip
{
    public static string[] DecompressToLines(MemoryStream compressed)
    {
        var bytes = compressed.ToArray();
        if (bytes.Length == 0) return [];

        using var input = new MemoryStream(bytes);
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        var text = reader.ReadToEnd();
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
