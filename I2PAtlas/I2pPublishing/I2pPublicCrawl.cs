using System.Collections.Immutable;
using System.Text.Json.Serialization;
using I2PAtlas.I2pProbing;

namespace I2PAtlas.I2pPublishing;

public enum I2pPublicCrawlEntryKind { KnownSeed, DiscoveredHost }
public enum I2pPublicCrawlHostForm { Friendly, ClassicBase32 }

// Observations through an unauthenticated proxy, never liveness or origin-authentication claims.
public sealed record I2pPublicCrawlObservation(
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

// Closed, distinct shapes: a discovered reference cannot acquire imported destination material.
[JsonDerivedType(typeof(I2pPublicCrawlEntry.KnownSeed))]
[JsonDerivedType(typeof(I2pPublicCrawlEntry.DiscoveredHost))]
public abstract class I2pPublicCrawlEntry
{
    private I2pPublicCrawlEntry(string hostname, I2pPublicCrawlObservation observation)
    { Hostname = hostname; Observation = observation; }
    public abstract I2pPublicCrawlEntryKind Kind { get; }
    public string Hostname { get; }
    public abstract int Depth { get; }
    public I2pPublicCrawlObservation Observation { get; }

    public sealed class KnownSeed : I2pPublicCrawlEntry
    {
        public override I2pPublicCrawlEntryKind Kind => I2pPublicCrawlEntryKind.KnownSeed;
        public override int Depth => 0;
        public string DestinationHostname { get; }
        internal KnownSeed(string hostname, string destinationHostname, I2pPublicCrawlObservation observation) : base(hostname, observation)
        { DestinationHostname = destinationHostname; }
    }

    public sealed class DiscoveredHost : I2pPublicCrawlEntry
    {
        public override I2pPublicCrawlEntryKind Kind => I2pPublicCrawlEntryKind.DiscoveredHost;
        public override int Depth => 1;
        public I2pPublicCrawlHostForm HostForm { get; }
        public string ReferrerHostname { get; }
        public string ReferrerDestinationHostname { get; }
        internal DiscoveredHost(string hostname, I2pPublicCrawlHostForm hostForm, string referrerHostname,
            string referrerDestinationHostname, I2pPublicCrawlObservation observation) : base(hostname, observation)
        { HostForm = hostForm; ReferrerHostname = referrerHostname; ReferrerDestinationHostname = referrerDestinationHostname; }
    }
}

/// <summary>Separate public crawl allowlist. Contains no raw objects, Base64 destinations or operational provenance.</summary>
public sealed class I2pPublicCrawl
{
    public const string CurrentSchema = "i2patlas.public-crawl";
    public const int CurrentVersion = 1;
    public string Schema => CurrentSchema;
    public int Version => CurrentVersion;
    public DateTimeOffset CrawlStartedAtUtc { get; }
    public DateTimeOffset CrawlCompletedAtUtc { get; }
    public int KnownSeedCount { get; }
    public int DiscoveredHostCount => Entries.Length - KnownSeedCount;
    public ImmutableArray<I2pPublicCrawlEntry> Entries { get; }

    internal I2pPublicCrawl(DateTimeOffset started, DateTimeOffset completed, ImmutableArray<I2pPublicCrawlEntry> entries)
    {
        CrawlStartedAtUtc = started;
        CrawlCompletedAtUtc = completed;
        Entries = entries;
        KnownSeedCount = entries.Count(e => e is I2pPublicCrawlEntry.KnownSeed);
    }
}
