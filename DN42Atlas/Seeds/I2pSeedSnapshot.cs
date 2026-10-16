using System.Collections.Immutable;
using DN42Atlas.AddressBook;

namespace DN42Atlas.Seeds;

/// <summary>Private identity of the exact validated snapshot file, distinct from its addressbook source hash.</summary>
public sealed record LoadedSeedSnapshot(I2pSeedSnapshot Snapshot, string FileSha256, long FileByteLength);

/// <summary>Private I2P input state. Never serialize this model into public scan artifacts.</summary>
public sealed class I2pSeedSnapshot
{
    public const string CurrentSchema = "i2patlas.seed-snapshot";
    public const int CurrentVersion = 1;

    public string Schema => CurrentSchema;
    public int Version => CurrentVersion;
    public DateTimeOffset ImportedAtUtc { get; }
    public SeedFileSource Source { get; }
    public ImmutableArray<I2pSeed> Seeds { get; }

    internal I2pSeedSnapshot(DateTimeOffset importedAtUtc, SeedFileSource source, ImmutableArray<I2pSeed> seeds)
    {
        ImportedAtUtc = importedAtUtc.ToUniversalTime();
        Source = source;
        Seeds = seeds;
    }
}

/// <summary>Private provenance for the exact byte stream consumed by a successful file import.</summary>
public sealed class SeedFileSource
{
    public string Path { get; }
    public string Sha256 { get; }
    public long ByteLength { get; }

    internal SeedFileSource(string path, string sha256, long byteLength)
    {
        Path = path;
        Sha256 = sha256;
        ByteLength = byteLength;
    }
}
