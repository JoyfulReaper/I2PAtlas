using System.Collections.Immutable;
using System.Text.Json.Serialization;
using I2PAtlas.I2pProbing;

namespace I2PAtlas.I2pScanning;

public sealed class I2pScanSettings
{
    public const int DefaultConcurrency = 2;
    public const int MaximumConcurrency = 4;
    public int Concurrency { get; }
    public int RequestTimeoutMilliseconds { get; }
    public int MaximumSeeds { get; }

    [JsonConstructor]
    public I2pScanSettings(int concurrency = DefaultConcurrency, int requestTimeoutMilliseconds = 10000, int maximumSeeds = 10000)
    {
        if (concurrency is < 1 or > MaximumConcurrency) throw new ArgumentOutOfRangeException(nameof(concurrency));
        if (requestTimeoutMilliseconds is < 1 or > 60000) throw new ArgumentOutOfRangeException(nameof(requestTimeoutMilliseconds));
        if (maximumSeeds is < 1 or > 10000) throw new ArgumentOutOfRangeException(nameof(maximumSeeds));
        Concurrency = concurrency;
        RequestTimeoutMilliseconds = requestTimeoutMilliseconds;
        MaximumSeeds = maximumSeeds;
    }
}

// All types in this file are private scan state, never a public projection.
public sealed record I2pScanSeedInput(string Schema, int Version, string FileSha256, long FileByteLength,
    DateTimeOffset ImportedAtUtc, string AddressBookSha256, long AddressBookByteLength);
public sealed record I2pScanProxy(string Address, int Port);
public sealed record I2pScanError(string Phase, string Category, string Detail);

public sealed record I2pRawSeedResult(
    string Hostname, string Destination, string DestinationHostname,
    DateTimeOffset StartedAtUtc, DateTimeOffset CompletedAtUtc, double DurationMilliseconds,
    I2pRequestOutcome? RobotsRequestOutcome, I2pOriginEvidence RobotsOriginEvidence,
    I2pProxyResponseClassification RobotsProxyClassification, I2pRobotsObservation RobotsObservation,
    I2pResponseObservation? RobotsResponse,
    bool? HomepageRequestAttempted, I2pHomepageDisposition? HomepageDisposition,
    I2pRequestOutcome? HomepageRequestOutcome, I2pOriginEvidence HomepageOriginEvidence,
    I2pProxyResponseClassification? HomepageProxyClassification, I2pResponseObservation? HomepageResponse,
    I2pPageMetadata? HomepageMetadata, ImmutableArray<I2pScanError> Errors);

public sealed record I2pScanMetrics(double TotalDurationMilliseconds,
    ImmutableSortedDictionary<string, int> RobotsRequestOutcomes,
    ImmutableSortedDictionary<string, int> RobotsProxyClassifications,
    ImmutableSortedDictionary<string, int> HomepageRequestOutcomes,
    ImmutableSortedDictionary<string, int> HomepageProxyClassifications,
    int HomepageAttemptedCount, int HomepageResponseCount, int UnexpectedProbeFailureCount, int MaximumActiveSeeds);

/// <summary>Versioned immutable private artifact. A future publisher needs its own strict reader and explicit projection.</summary>
public sealed class I2pRawScanArtifact
{
    public const string CurrentSchema = "i2patlas.raw-scan";
    public const int CurrentVersion = 1;
    public string Schema { get; }
    public int Version { get; }
    public DateTimeOffset StartedAtUtc { get; }
    public DateTimeOffset CompletedAtUtc { get; }
    public I2pScanSeedInput SeedInput { get; }
    public I2pScanProxy Proxy { get; }
    public I2pScanSettings Settings { get; }
    public int SnapshotSeedCount { get; }
    public int TotalSeeds => Results.Length;
    public bool SeedLimitApplied => TotalSeeds < SnapshotSeedCount;
    public ImmutableArray<I2pRawSeedResult> Results { get; }
    public I2pScanMetrics Metrics { get; }

    [JsonConstructor]
    public I2pRawScanArtifact(string schema, int version, DateTimeOffset startedAtUtc, DateTimeOffset completedAtUtc,
        I2pScanSeedInput seedInput, I2pScanProxy proxy, I2pScanSettings settings, int snapshotSeedCount,
        ImmutableArray<I2pRawSeedResult> results, I2pScanMetrics metrics)
    {
        if (schema != CurrentSchema || version != CurrentVersion) throw new InvalidDataException("Unsupported I2P raw scan schema/version.");
        ArgumentNullException.ThrowIfNull(seedInput);
        ArgumentNullException.ThrowIfNull(proxy);
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(metrics);
        if (results.IsDefault || snapshotSeedCount < results.Length || snapshotSeedCount > 10000)
            throw new InvalidDataException("Invalid I2P raw scan result count.");
        Schema = schema;
        Version = version;
        StartedAtUtc = startedAtUtc.ToUniversalTime();
        CompletedAtUtc = completedAtUtc.ToUniversalTime();
        SeedInput = seedInput;
        Proxy = proxy;
        Settings = settings;
        SnapshotSeedCount = snapshotSeedCount;
        Results = results;
        Metrics = metrics;
    }
}
