using System.Collections.Immutable;
using System.Text.Json;
using I2PAtlas.I2pCrawling;
using I2PAtlas.I2pProbing;
using I2PAtlas.I2pPublishing;
using static I2PAtlas.Tests.CrawlTestData;

namespace I2PAtlas.Tests;

internal static class CrawlProjectionTestData
{
    internal static I2pRawCrawlArtifact PrivateArtifact()
    {
        var raw = Artifact();
        var e = raw.KnownSeedResults[0].Evidence;
        const string type = "Text/HTML; charset=PRIVATE_CHARSET_SENTINEL";
        var links = new[] { "http://child.i2p/PRIVATE_PATH?private=PRIVATE_QUERY", "http://child.i2p/other",
            $"http://{ClassicB32}/PRIVATE_PATH", "https://private-clearnet.example/PRIVATE_CLEARNET" }
            .Order(StringComparer.Ordinal).Select(u => new I2pDiscoveredLink(u,
                u.Contains("private-clearnet", StringComparison.Ordinal) ? I2pDiscoveredLinkKind.Clearnet : I2pDiscoveredLinkKind.I2p)).ToImmutableArray();
        var first = raw.KnownSeedResults[0] with { Evidence = e with
        {
            RobotsResponse = e.RobotsResponse! with { RedirectLocation = "http://secret.i2p/PRIVATE_REDIRECT" },
            HomepageResponse = e.HomepageResponse! with { ContentType = type },
            HomepageMetadata = e.HomepageMetadata! with { ContentType = type, Links = links,
                Title = "\0\t\u202e<b>literal</b>\u2028" + new string('x', 180) }
        } };
        var second = raw.KnownSeedResults[1] with { Hostname = first.Hostname,
            Evidence = I2pCrawlEvidence.FromObservation(null, e.StartedAtUtc, e.CompletedAtUtc, e.DurationMilliseconds) };
        return Refresh(raw with { KnownSeedResults = new[] { first, second }.OrderBy(r => r.Destination, StringComparer.Ordinal).ToImmutableArray(),
            Proxy = new("127.0.0.2", 43891), Discovery = new(3, 2, false) });
    }

    internal static I2pRawCrawlArtifact Refresh(I2pRawCrawlArtifact raw) => raw with
    { Metrics = I2pCrawlMetrics.Calculate(raw.Evidence, raw.Metrics.TotalDurationMilliseconds, raw.Metrics.MaximumActiveProbes) };
    internal static async Task<string> WriteRawAsync(TemporaryFiles files, I2pRawCrawlArtifact? raw = null)
    {
        var path = Path.Combine(files.DirectoryPath, "private-raw.json");
        await I2pRawCrawlFile.WriteAsync(path, raw ?? PrivateArtifact(), default);
        return path;
    }
    internal static string PublicJson() => JsonSerializer.Serialize(I2pPublicCrawlProjector.Project(Artifact(), default), I2pCrawlProjectionService.JsonOptions);
}
