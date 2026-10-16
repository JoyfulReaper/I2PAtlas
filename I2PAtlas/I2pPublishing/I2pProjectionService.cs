using System.Text.Json;
using System.Text.Json.Serialization;
using I2PAtlas.IO;

namespace I2PAtlas.I2pPublishing;

/// <summary>Purely local private-evidence to public-allowlist boundary.</summary>
public sealed class I2pProjectionService
{
    internal static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter(allowIntegerValues: false) }
    };
    private readonly Action<string>? beforeReplace;
    public I2pProjectionService() { }
    internal I2pProjectionService(Action<string> beforeReplace) => this.beforeReplace = beforeReplace;

    public async Task<I2pPublicScan> ProjectAsync(string inputPath, string outputPath, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var input = LocalPath(inputPath);
        var output = LocalPath(outputPath);
        if (input.Equals(output, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new ArgumentException("Public output must be separate from private raw input.");
        RejectLinks(input);
        RejectLinks(output);
        var directory = Path.GetDirectoryName(output)!;
        if (!Directory.Exists(directory)) throw new DirectoryNotFoundException();
        if (Directory.Exists(output)) throw new IOException("Public output must be a file.");
        var raw = await I2pRawScanReader.ReadAsync(input, cancellationToken);
        var projection = I2pPublicProjector.Project(raw, cancellationToken);
        var staged = Path.Combine(directory, $".i2p-public-{Guid.NewGuid():N}.tmp");
        var owned = false;
        try
        {
            await using (var file = PrivateFile.CreateNew(staged))
            {
                owned = true;
                using var bounded = new BoundedOutput(file);
                await JsonSerializer.SerializeAsync(bounded, projection, JsonOptions, cancellationToken);
                await file.FlushAsync(cancellationToken);
                file.Flush(flushToDisk: true);
            }
            beforeReplace?.Invoke(staged);
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staged, output, overwrite: true);
            owned = false;
        }
        finally { if (owned) File.Delete(staged); }
        return projection;
    }

    internal static string LocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if ((path.Length >= 2 && (path[0] is '\\' or '/') && (path[1] is '\\' or '/')) || path.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("Projection requires explicit local paths.");
        return Path.GetFullPath(path);
    }

    private static void RejectLinks(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new ArgumentException("Projection paths must not use filesystem links.");
        for (var parent = new DirectoryInfo(Path.GetDirectoryName(path)!); parent != null && parent.Exists; parent = parent.Parent)
            if ((parent.Attributes & FileAttributes.ReparsePoint) != 0) throw new ArgumentException("Projection paths must not use filesystem links.");
    }

    private sealed class BoundedOutput(FileStream file) : Stream
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
            if (written + count > I2pRawScanReader.MaxArtifactBytes) throw new InvalidDataException("Public output exceeds 128 MiB.");
            written += count;
        }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
