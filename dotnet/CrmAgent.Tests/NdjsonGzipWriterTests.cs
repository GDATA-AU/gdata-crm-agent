using System.IO.Compression;
using System.Text;
using System.Text.Json;
using CrmAgent.Services;

namespace CrmAgent.Tests;

public class NdjsonGzipWriterTests
{
    [Fact]
    public async Task WritesValidGzipCompressedNdjson()
    {
        using var output = new MemoryStream();
        await using (var writer = new NdjsonGzipWriter(output, leaveOpen: true))
        {
            await writer.WriteRowAsync(new Dictionary<string, object?> { ["id"] = 1, ["name"] = "Alice" });
            await writer.WriteRowAsync(new Dictionary<string, object?> { ["id"] = 2, ["name"] = "Bob" });
        }

        var lines = DecompressToLines(output);
        Assert.Equal(2, lines.Length);

        var row1 = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(lines[0])!;
        Assert.Equal(1, row1["id"].GetInt32());
        Assert.Equal("Alice", row1["name"].GetString());

        var row2 = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(lines[1])!;
        Assert.Equal(2, row2["id"].GetInt32());
        Assert.Equal("Bob", row2["name"].GetString());
    }

    [Fact]
    public async Task ProducesEmptyOutputForZeroRows()
    {
        using var output = new MemoryStream();
        await using (var writer = new NdjsonGzipWriter(output, leaveOpen: true))
        {
            // Write nothing
        }

        // With zero rows, gzip output should decompress to empty string.
        // .NET's GZipStream may produce a minimal gzip (or nothing) for zero input.
        if (output.ToArray().Length > 0)
        {
            var text = DecompressToString(output);
            Assert.Equal("", text);
        }
        else
        {
            // Zero-length output for zero-write gzip is acceptable in .NET.
            Assert.Empty(output.ToArray());
        }
    }

    [Fact]
    public async Task IncludesRowHashField()
    {
        using var output = new MemoryStream();
        await using (var writer = new NdjsonGzipWriter(output, leaveOpen: true))
        {
            await writer.WriteRowAsync(new Dictionary<string, object?>
            {
                ["id"] = 1, ["name"] = "Alice", ["_rowHash"] = "abc123"
            });
        }

        var lines = DecompressToLines(output);
        var parsed = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(lines[0])!;
        Assert.Equal("abc123", parsed["_rowHash"].GetString());
    }

    [Fact]
    public async Task EachLineIsCompleteJsonObject()
    {
        using var output = new MemoryStream();
        await using (var writer = new NdjsonGzipWriter(output, leaveOpen: true))
        {
            for (var i = 0; i < 5; i++)
                await writer.WriteRowAsync(new Dictionary<string, object?> { ["i"] = i });
        }

        var lines = DecompressToLines(output);
        Assert.Equal(5, lines.Length);
        foreach (var line in lines)
        {
            // Must parse as valid JSON without throwing
            JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(line);
        }
    }

    private static string DecompressToString(MemoryStream compressed)
    {
        compressed.Position = 0;
        using var gzip = new GZipStream(compressed, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new StreamReader(gzip, Encoding.UTF8);
        return reader.ReadToEnd();
    }

    private static string[] DecompressToLines(MemoryStream compressed)
    {
        var text = DecompressToString(compressed);
        return text.Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }
}
