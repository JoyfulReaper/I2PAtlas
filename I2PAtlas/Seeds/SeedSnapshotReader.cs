using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text.Json;
using I2PAtlas.AddressBook;

namespace I2PAtlas.Seeds;

/// <summary>Bounded, local-only validation of private seed state; source metadata is never opened.</summary>
public static class SeedSnapshotReader
{
    // The importer accepts 16 Mi UTF-16 addressbook characters. Its ASCII seed data
    // plus JSON framing fits comfortably here without accepting unbounded JSON.
    public const int MaxSnapshotBytes = 32 * 1024 * 1024;

    public static async Task<I2pSeedSnapshot> ReadAsync(string path, CancellationToken cancellationToken = default) =>
        (await ReadWithIdentityAsync(path, cancellationToken)).Snapshot;

    /// <summary>Hashes exactly the same bounded file bytes that pass strict validation, without reopening a path.</summary>
    public static async Task<LoadedSeedSnapshot> ReadWithIdentityAsync(string path, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if ((path.Length >= 2 && (path[0] is '\\' or '/') && (path[1] is '\\' or '/')) ||
            path.Contains("://", StringComparison.Ordinal))
            throw new ArgumentException("Seed snapshots require a local file path, not a URL, UNC or device path.", nameof(path));

        cancellationToken.ThrowIfCancellationRequested();
        await using var input = new FileStream(Path.GetFullPath(path), FileMode.Open, FileAccess.Read,
            FileShare.Read, 8192, FileOptions.Asynchronous | FileOptions.SequentialScan);
        if (input.Length > MaxSnapshotBytes) throw Invalid("Snapshot exceeds the 32 MiB byte limit.");
        using var bytes = new MemoryStream();
        var buffer = new byte[8192];
        int count;
        while ((count = await input.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (bytes.Length + count > MaxSnapshotBytes) throw Invalid("Snapshot exceeds the 32 MiB byte limit.");
            bytes.Write(buffer, 0, count);
        }
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            using var document = JsonDocument.Parse(bytes.GetBuffer().AsMemory(0, (int)bytes.Length),
                new JsonDocumentOptions { MaxDepth = 8 });
            var snapshot = Validate(document.RootElement, cancellationToken);
            var hash = Convert.ToHexString(SHA256.HashData(bytes.GetBuffer().AsSpan(0, (int)bytes.Length)));
            cancellationToken.ThrowIfCancellationRequested();
            return new LoadedSeedSnapshot(snapshot, hash, bytes.Length);
        }
        // JsonDocument can defer UTF-8/escaped Unicode decoding until a property
        // name or string is accessed; that failure uses InvalidOperationException.
        // All JSON kinds and required properties are checked before typed access.
        catch (Exception ex) when (ex is JsonException or InvalidOperationException)
        {
            throw Invalid("Snapshot must be valid UTF-8 JSON with valid Unicode and a maximum depth of 8.");
        }
    }

    private static I2pSeedSnapshot Validate(JsonElement root, CancellationToken token)
    {
        Fields(root, "snapshot", "schema", "version", "importedAtUtc", "source", "seeds");
        if (Text(root, "schema") != I2pSeedSnapshot.CurrentSchema) throw Invalid("Unsupported snapshot schema.");
        var versionField = root.GetProperty("version");
        if (versionField.ValueKind != JsonValueKind.Number || !versionField.TryGetInt32(out var version) || version != I2pSeedSnapshot.CurrentVersion)
            throw Invalid("Unsupported snapshot version.");
        var timestamp = root.GetProperty("importedAtUtc");
        var timestampText = Text(root, "importedAtUtc");
        if (timestampText.Length > 64 || !(timestampText.EndsWith('Z') || timestampText.EndsWith("+00:00", StringComparison.Ordinal)) ||
            !timestamp.TryGetDateTimeOffset(out var importedAt) || importedAt.Offset != TimeSpan.Zero)
            throw Invalid("importedAtUtc must be an explicit UTC timestamp.");

        var source = root.GetProperty("source");
        Fields(source, "source", "path", "sha256", "byteLength");
        var sourcePath = Text(source, "path");
        if (string.IsNullOrWhiteSpace(sourcePath) || sourcePath.Length > AddressBookParser.MaxSourceCharacters || sourcePath.Any(char.IsControl))
            throw Invalid("source.path must be nonempty, bounded and free of control characters.");
        var hash = Text(source, "sha256");
        if (hash.Length != 64 || !hash.All(char.IsAsciiHexDigit)) throw Invalid("source.sha256 must contain 64 hexadecimal characters.");
        var lengthField = source.GetProperty("byteLength");
        if (lengthField.ValueKind != JsonValueKind.Number || !lengthField.TryGetInt64(out var byteLength) || byteLength < 0)
            throw Invalid("source.byteLength must be a nonnegative 64-bit integer.");

        var entries = root.GetProperty("seeds");
        if (entries.ValueKind != JsonValueKind.Array || entries.GetArrayLength() > AddressBookParser.MaxSeeds)
            throw Invalid("seeds must be an array with at most 10000 entries, including duplicates.");
        var seeds = new HashSet<I2pSeed>();
        var index = 0;
        foreach (var entry in entries.EnumerateArray())
        {
            token.ThrowIfCancellationRequested();
            Fields(entry, "seed", "hostname", "destination");
            var hostname = Text(entry, "hostname");
            var destination = Text(entry, "destination");
            I2pSeed seed;
            try { seed = new I2pSeed(hostname, destination); }
            catch (FormatException) { throw Invalid($"Invalid seed at index {index}."); }
            seeds.Add(seed);
            index++;
        }
        token.ThrowIfCancellationRequested();
        return new I2pSeedSnapshot(importedAt, new SeedFileSource(sourcePath, hash, byteLength),
            seeds.OrderBy(seed => seed.Hostname, StringComparer.Ordinal)
                .ThenBy(seed => seed.Destination, StringComparer.Ordinal).ToImmutableArray());
    }

    // Reject ambiguous duplicate JSON properties, case variants and unknown fields.
    private static void Fields(JsonElement value, string context, params string[] required)
    {
        if (value.ValueKind != JsonValueKind.Object) throw Invalid($"{context} must be an object.");
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in value.EnumerateObject())
            if (!required.Contains(property.Name, StringComparer.Ordinal) || !seen.Add(property.Name))
                throw Invalid($"{context} contains an unknown or duplicate property.");
        if (seen.Count != required.Length) throw Invalid($"{context} is missing a required property.");
    }

    private static string Text(JsonElement value, string property)
    {
        var text = value.GetProperty(property);
        if (text.ValueKind != JsonValueKind.String) throw Invalid($"{property} must be a string.");
        return text.GetString()!;
    }

    private static InvalidDataException Invalid(string message) => new(message);
}
