using System.Collections.Immutable;
using System.Text.Json.Serialization;
using I2PAtlas.AddressBook;
using I2PAtlas.I2pProbing;
using I2PAtlas.I2pTransport;

namespace I2PAtlas.I2pCrawling;

internal sealed class I2pCrawlDiscoverySettings
{
    internal const int DefaultMaximumDiscoveredHosts = 100;
    internal const int HardMaximumDiscoveredHosts = 1000;
    internal int MaximumDiscoveredHosts { get; }

    internal I2pCrawlDiscoverySettings(int maximumDiscoveredHosts = DefaultMaximumDiscoveredHosts)
    {
        if (maximumDiscoveredHosts is < 0 or > HardMaximumDiscoveredHosts)
            throw new ArgumentOutOfRangeException(nameof(maximumDiscoveredHosts));
        MaximumDiscoveredHosts = maximumDiscoveredHosts;
    }
}

// Only explicitly supplied selected/probed parents provide discovery evidence.
internal sealed record I2pCrawlSeedObservation(I2pProbeTarget.KnownSeed Target, I2pProbeObservation Observation);
internal sealed record I2pCrawlParentObservation(I2pCrawlReferrer Referrer, I2pProbeObservation Observation);

// Exclusive shapes: known parent has destinationHostname; host-only parent has hostForm.
internal readonly record struct I2pCrawlReferrer(string Hostname,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] string? DestinationHostname,
    [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)] I2pDiscoveredHostForm? HostForm = null) : IComparable<I2pCrawlReferrer>
{
    internal static I2pCrawlReferrer FromSeed(I2pSeed seed)
    {
        var identity = I2pSeedIdentity.FromSeed(seed);
        return new(identity.Hostname, identity.DestinationHostname);
    }

    public int CompareTo(I2pCrawlReferrer other)
    {
        var hostnameOrder = string.CompareOrdinal(Hostname, other.Hostname);
        return hostnameOrder != 0 ? hostnameOrder : string.CompareOrdinal(DestinationHostname, other.DestinationHostname);
    }
}

// No original URL, destination material, body or backlink collection is retained.
internal sealed record I2pCrawlWorkItem(I2pProbeTarget.DiscoveredHost Target, I2pCrawlReferrer FirstReferrer, int Depth = 1);

// Reference count includes repeated eligible unknown URLs/parents, not unique rejected hosts.
internal sealed record I2pCrawlFrontier(
    ImmutableArray<I2pCrawlWorkItem> WorkItems,
    long EligibleUnknownReferenceCount,
    bool AdmissionLimitApplied);
