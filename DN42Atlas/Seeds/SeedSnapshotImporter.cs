using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using DN42Atlas.AddressBook;
using DN42Atlas.IO;

namespace DN42Atlas.Seeds;

/// <summary>Local addressbook-to-private-snapshot boundary; independent of discovery and transport.</summary>
public sealed class SeedSnapshotImporter(TimeProvider? timeProvider = null)
{
    private readonly TimeProvider timeProvider = timeProvider ?? TimeProvider.System;
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    public async Task<I2pSeedSnapshot> ImportAsync(string inputPath, string outputPath,
        CancellationToken cancellationToken = default)
    {
        var input = LocalPath(inputPath);
        var output = LocalPath(outputPath);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (input.Equals(output, comparison))
            throw new ArgumentException("Seed snapshot output must not overwrite the input addressbook.", nameof(outputPath));

        cancellationToken.ThrowIfCancellationRequested();
        var snapshot = ReadSnapshot(input);
        cancellationToken.ThrowIfCancellationRequested();

        // Require an existing output directory. Stage privately on that same filesystem;
        // no previous output is touched until parsing, serialization and flushing succeed.
        var staged = Path.Combine(Path.GetDirectoryName(output)!, $".i2p-seeds-{Guid.NewGuid():N}.tmp");
        var stagedOwned = false;
        try
        {
            await using (var stream = PrivateFile.CreateNew(staged))
            {
                stagedOwned = true;
                await JsonSerializer.SerializeAsync(stream, snapshot, JsonOptions, cancellationToken);
                await stream.FlushAsync(cancellationToken);
                stream.Flush(flushToDisk: true);
            }
            cancellationToken.ThrowIfCancellationRequested();
            File.Move(staged, output, overwrite: true);
            return snapshot;
        }
        finally
        {
            if (stagedOwned) File.Delete(staged);
        }
    }

    private I2pSeedSnapshot ReadSnapshot(string path)
    {
        using var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var sha256 = SHA256.Create();
        using var hashing = new CryptoStream(input, sha256, CryptoStreamMode.Read, leaveOpen: true);
        using var reader = new StreamReader(hashing, new UTF8Encoding(false, true),
            detectEncodingFromByteOrderMarks: false);
        // Parse reads through EOF, so the hash includes every consumed source byte:
        // BOM, comments, whitespace, duplicate entries and original line endings.
        var parsed = AddressBookParser.Parse(reader, path);
        var source = new SeedFileSource(parsed.Source, Convert.ToHexString(sha256.Hash!), input.Position);
        return new I2pSeedSnapshot(timeProvider.GetUtcNow(), source,
            parsed.Seeds);
    }

    private static string LocalPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if ((path.Length >= 2 && (path[0] is '\\' or '/') && (path[1] is '\\' or '/')) ||
            path.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("Seed imports require local filesystem paths, not URLs, UNC or device paths.", nameof(path));
        return Path.GetFullPath(path);
    }
}
