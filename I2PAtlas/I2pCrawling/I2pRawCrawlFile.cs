using System.Text.Json;
using System.Text.Json.Serialization;
using I2PAtlas.IO;

namespace I2PAtlas.I2pCrawling;

internal static class I2pRawCrawlFile
{
    internal const long MaxArtifactBytes = 128L * 1024 * 1024;
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };

    internal static (string Input, string Output) ValidatePaths(string inputPath, string outputPath)
    {
        var input = LocalPath(inputPath);
        var output = LocalPath(outputPath);
        if (input.Equals(output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("I2P crawl output must not overwrite its seed snapshot.");
        var segments = output.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (segments.Any(s => new[] { "published", "public", "wwwroot", "site" }.Contains(s, StringComparer.OrdinalIgnoreCase)))
            throw new ArgumentException("I2P raw crawls must stay outside public/served directories.");
        var directory = Path.GetDirectoryName(output)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException("I2P raw crawl output directory must already exist.");
        if (Directory.Exists(output)) throw new IOException("I2P raw crawl output must be a file.");
        RejectReparsePoints(input);
        RejectReparsePoints(output);
        return (input, output);
    }

    internal static async Task WriteAsync(string output, I2pRawCrawlArtifact artifact, CancellationToken token,
        Action<string>? beforeReplace = null, long byteLimit = MaxArtifactBytes)
    {
        if (byteLimit is < 1 or > MaxArtifactBytes) throw new ArgumentOutOfRangeException(nameof(byteLimit));
        var staged = Path.Combine(Path.GetDirectoryName(output)!, $".i2p-crawl-{Guid.NewGuid():N}.tmp");
        var owned = false;
        try
        {
            token.ThrowIfCancellationRequested();
            await using (var file = PrivateFile.CreateNew(staged))
            {
                owned = true;
                using var bounded = new BoundedWriteStream(file, byteLimit);
                await JsonSerializer.SerializeAsync(bounded, artifact, JsonOptions, token);
                await file.FlushAsync(token);
                file.Flush(flushToDisk: true);
            }
            // Internal test seam observes a complete, flushed, closed staging file.
            // Validate the complete bounded staged bytes before any installation.
            _ = await I2pRawCrawlReader.ReadAsync(staged, token);
            beforeReplace?.Invoke(staged);
            token.ThrowIfCancellationRequested();
            File.Move(staged, output, overwrite: true);
            owned = false;
        }
        finally
        {
            if (owned) File.Delete(staged);
        }
    }

    internal static string LocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if ((path.Length >= 2 && (path[0] is '\\' or '/') && (path[1] is '\\' or '/')) || path.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("I2P crawls require explicit local file paths.");
        return Path.GetFullPath(path);
    }

    private static void RejectReparsePoints(string path)
    {
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("I2P crawl paths must not use filesystem links.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent != null; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("I2P crawl paths must not use filesystem links.");
    }

    private sealed class BoundedWriteStream(FileStream file, long byteLimit) : Stream
    {
        private long written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() => file.Flush();
        public override void Write(byte[] buffer, int offset, int count)
        {
            Reserve(count);
            file.Write(buffer, offset, count);
        }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken token = default)
        {
            Reserve(buffer.Length);
            return file.WriteAsync(buffer, token);
        }
        private void Reserve(int count)
        {
            if (written + count > byteLimit) throw new InvalidDataException("I2P raw crawl exceeds the 128 MiB artifact limit.");
            written += count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
