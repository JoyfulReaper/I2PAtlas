using System.Collections.Immutable;
using I2PAtlas.I2pScanning;
using static I2PAtlas.I2pPublishing.I2pPublicProjectionPolicy;

namespace I2PAtlas.I2pPublishing;

internal static class I2pPublicProjector
{
    internal const int MaxTitleCharacters = I2pPublicProjectionPolicy.MaxTitleCharacters;

    // Called only after strict raw validation. No raw object or dictionary enters the public graph.
    internal static I2pPublicScan Project(I2pRawScanArtifact raw, CancellationToken token)
    {
        var counts = raw.Results.GroupBy(r => r.Hostname, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => g.Select(r => r.DestinationHostname).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);
        var entries = ImmutableArray.CreateBuilder<I2pPublicEntry>(raw.Results.Length);
        var identities = new HashSet<(string Hostname, string DestinationHostname)>();
        foreach (var result in raw.Results)
        {
            token.ThrowIfCancellationRequested();
            if (!identities.Add((result.Hostname, result.DestinationHostname)))
                throw new InvalidDataException("Duplicate public seed identity; scanned observations cannot be merged.");
            var links = DiscoveredHostnames(result.HomepageMetadata);
            entries.Add(new(result.Hostname, result.DestinationHostname, counts[result.Hostname],
                Summary(result.RobotsRequestOutcome, result.RobotsProxyClassification, result.RobotsObservation,
                    result.HomepageRequestAttempted, result.HomepageResponse?.StatusCode),
                result.RobotsRequestOutcome, result.RobotsProxyClassification, result.RobotsObservation, result.RobotsResponse?.StatusCode,
                result.HomepageRequestAttempted, result.HomepageRequestOutcome, result.HomepageProxyClassification,
                result.HomepageResponse?.StatusCode, MediaType(result.HomepageResponse?.ContentType), Title(result.HomepageMetadata?.Title),
                links, result.HomepageMetadata == null || result.HomepageMetadata.ExtractionIncomplete || result.HomepageMetadata.BodyTruncated));
        }
        // Public ordering does not depend on private Base64 spelling. Never choose a preferred destination.
        return new(raw.StartedAtUtc, raw.CompletedAtUtc, entries.OrderBy(e => e.Hostname, StringComparer.Ordinal)
            .ThenBy(e => e.DestinationHostname, StringComparer.Ordinal).ToImmutableArray());
    }

}
