using System.Text.Json;
using System.Text.Json.Serialization;
using I2PAtlas.IO;
using I2PAtlas.I2pCrawling;

namespace I2PAtlas.I2pPublishing;

/// <summary>Local private raw-crawl validation and explicit public projection; never constructs a transport.</summary>
public sealed class I2pCrawlProjectionService
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly Action<string>? beforeValidate;
    private readonly Action<string>? beforeReplace;
    private readonly long outputByteLimit = I2pPublicCrawlReader.MaxArtifactBytes;
    public I2pCrawlProjectionService() { }
    // Internal fault injection at the closed staging boundary; production cannot raise the hard cap.
    internal I2pCrawlProjectionService(Action<string>? beforeValidate, Action<string>? beforeReplace = null,
        long outputByteLimit = I2pPublicCrawlReader.MaxArtifactBytes)
    {
        if (outputByteLimit is < 1 or > I2pPublicCrawlReader.MaxArtifactBytes) throw new ArgumentOutOfRangeException(nameof(outputByteLimit));
        this.beforeValidate = beforeValidate;
        this.beforeReplace = beforeReplace;
        this.outputByteLimit = outputByteLimit;
    }

    public async Task<I2pPublicCrawl> ProjectAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = LocalPathWithoutLinks(inputPath);
        var output = LocalPathWithoutLinks(outputPath);
        if (input.Equals(output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Public crawl output must differ from private input.");
        var directory = Path.GetDirectoryName(output)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException();
        if (Directory.Exists(output)) throw new IOException("Public crawl output must be a file.");
        var raw = await I2pRawCrawlReader.ReadAsync(input, cancellationToken);
        var projection = I2pPublicCrawlProjector.Project(raw, cancellationToken);
        var staged = Path.Combine(directory, $".i2p-public-crawl-{Guid.NewGuid():N}.tmp");
        var owned = false;
        try
        {
            await using (var file = PrivateFile.CreateNew(staged))
            {
                owned = true;
                using var bounded = new BoundedOutput(file, outputByteLimit);
                await JsonSerializer.SerializeAsync(bounded, projection, JsonOptions, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            beforeValidate?.Invoke(staged);
            _ = await I2pPublicCrawlReader.ReadAsync(staged, cancellationToken);
            beforeReplace?.Invoke(staged);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staged, output, overwrite: true);
            owned = false;
        }
        finally { if (owned) File.Delete(staged); }
        return projection;
    }

    internal static string LocalPathWithoutLinks(string path)
    {
        path = I2pProjectionService.LocalPath(path);
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Projection paths must not use filesystem links.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent != null && parent.Exists; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0)
                throw new ArgumentException("Projection paths must not use filesystem links.");
        return path;
    }

    private sealed class BoundedOutput(FileStream file, long limit) : Stream
    {
        private long written;
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => written;
        public override long Position { get => written; set => throw new NotSupportedException(); }
        public override void Flush() => file.Flush();
        public override void Write(byte[] buffer, int offset, int count)
        { Reserve(count); file.Write(buffer, offset, count); }
        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        { Reserve(buffer.Length); return file.WriteAsync(buffer, cancellationToken); }
        private void Reserve(int count)
        {
            if (written + count > limit) throw new InvalidDataException("Public crawl output exceeds 128 MiB.");
            written += count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
