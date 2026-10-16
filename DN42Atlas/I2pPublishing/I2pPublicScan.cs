using System.Collections.Immutable;
using DN42Atlas.I2pProbing;

namespace DN42Atlas.I2pPublishing;

// Descriptions of observations through a proxy, never origin authentication or liveness claims.
public enum I2pPublicSummaryState
{
    HomepageResponseObserved,
    HomepageAttemptFailed,
    RobotsDisallowed,
    ProxyFailureObserved,
    RequestTimedOut,
    TransportFailure,
    RobotsRedirect,
    Ambiguous
}

/// <summary>Explicit public allowlist. Never add private raw state or arbitrary dictionaries here.</summary>
public sealed record I2pPublicEntry(
    string Hostname,
    string DestinationHostname,
    int DestinationsForHostname,
    I2pPublicSummaryState SummaryState,
    I2pRequestOutcome? RobotsRequestOutcome,
    I2pProxyResponseClassification RobotsProxyClassification,
    I2pRobotsObservation RobotsObservation,
    int? RobotsStatusCode,
    bool? HomepageRequestAttempted,
    I2pRequestOutcome? HomepageRequestOutcome,
    I2pProxyResponseClassification? HomepageProxyClassification,
    int? HomepageStatusCode,
    string? HomepageContentType,
    string? Title,
    ImmutableArray<string> DiscoveredI2pHostnames,
    bool LinkExtractionIncomplete);

public sealed class I2pPublicScan
{
    public const string CurrentSchema = "i2patlas.public-scan";
    public const int CurrentVersion = 1;
    public string Schema => CurrentSchema;
    public int Version => CurrentVersion;
    public DateTimeOffset ScanStartedAtUtc { get; }
    public DateTimeOffset ScanCompletedAtUtc { get; }
    public int SeedCount => Entries.Length;
    public int UniqueHostnameCount { get; }
    public ImmutableArray<I2pPublicEntry> Entries { get; }

    internal I2pPublicScan(DateTimeOffset started, DateTimeOffset completed, ImmutableArray<I2pPublicEntry> entries)
    {
        ScanStartedAtUtc = started;
        ScanCompletedAtUtc = completed;
        Entries = entries;
        UniqueHostnameCount = entries.Select(e => e.Hostname).Distinct(StringComparer.Ordinal).Count();
    }
}
