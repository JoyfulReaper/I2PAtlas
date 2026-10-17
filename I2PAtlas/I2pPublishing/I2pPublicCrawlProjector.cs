using System.Collections.Immutable;
using I2PAtlas.I2pCrawling;
using I2PAtlas.I2pTransport;
using static I2PAtlas.I2pPublishing.I2pPublicProjectionPolicy;

namespace I2PAtlas.I2pPublishing;

internal static class I2pPublicCrawlProjector
{
    // Only called after strict raw-crawl validation. All copied fields are explicitly allowlisted.
    internal static I2pPublicCrawl Project(I2pRawCrawlArtifact raw, CancellationToken token)
    {
        var entries = ImmutableArray.CreateBuilder<I2pPublicCrawlEntry>(raw.KnownSeedResults.Length + raw.DiscoveredHostResults.Length);
        foreach (var known in raw.KnownSeedResults.OrderBy(r => r.Hostname, StringComparer.Ordinal)
            .ThenBy(r => r.DestinationHostname, StringComparer.Ordinal))
        {
            token.ThrowIfCancellationRequested();
            entries.Add(new I2pPublicCrawlEntry.KnownSeed(known.Hostname, known.DestinationHostname, Observation(known.Evidence)));
        }
        foreach (var host in raw.DiscoveredHostResults)
        {
            token.ThrowIfCancellationRequested();
            var form = host.HostForm switch
            {
                I2pDiscoveredHostForm.Friendly => I2pPublicCrawlHostForm.Friendly,
                I2pDiscoveredHostForm.ClassicBase32 => I2pPublicCrawlHostForm.ClassicBase32,
                _ => throw new InvalidDataException("Invalid discovered host form.")
            };
            entries.Add(new I2pPublicCrawlEntry.DiscoveredHost(host.Hostname, form, host.Referrer.Hostname,
                host.Referrer.DestinationHostname, Observation(host.Evidence)));
        }
        return new(raw.CrawlStartedAtUtc, raw.CrawlCompletedAtUtc, entries.MoveToImmutable());
    }

    private static I2pPublicCrawlObservation Observation(I2pCrawlEvidence e) => new(
        Summary(e.RobotsRequestOutcome, e.RobotsProxyClassification, e.RobotsObservation, e.HomepageRequestAttempted, e.HomepageResponse?.StatusCode),
        e.RobotsRequestOutcome, e.RobotsProxyClassification, e.RobotsObservation, e.RobotsResponse?.StatusCode,
        e.HomepageRequestAttempted, e.HomepageRequestOutcome, e.HomepageProxyClassification, e.HomepageResponse?.StatusCode,
        MediaType(e.HomepageResponse?.ContentType), Title(e.HomepageMetadata?.Title), DiscoveredHostnames(e.HomepageMetadata),
        e.HomepageMetadata == null || e.HomepageMetadata.ExtractionIncomplete || e.HomepageMetadata.BodyTruncated);
}
