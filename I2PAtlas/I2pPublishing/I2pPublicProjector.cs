using System.Collections.Immutable;
using System.Globalization;
using System.Net.Http.Headers;
using System.Text;
using I2PAtlas.AddressBook;
using I2PAtlas.I2pProbing;
using I2PAtlas.I2pScanning;

namespace I2PAtlas.I2pPublishing;

internal static class I2pPublicProjector
{
    internal const int MaxTitleCharacters = 160;

    // Called only after strict raw validation. No raw object or dictionary enters the public graph.
    internal static I2pPublicScan Project(I2pRawScanArtifact raw, CancellationToken token)
    {
        var counts = raw.Results.GroupBy(r => r.Hostname, StringComparer.Ordinal).ToDictionary(g => g.Key,
            g => g.Select(r => r.DestinationHostname).Distinct(StringComparer.Ordinal).Count(), StringComparer.Ordinal);
        var entries = ImmutableArray.CreateBuilder<I2pPublicEntry>(raw.Results.Length);
        foreach (var result in raw.Results)
        {
            token.ThrowIfCancellationRequested();
            var links = (result.HomepageMetadata?.Links ?? []).Where(link => link.Kind == I2pDiscoveredLinkKind.I2p)
                .Select(link => I2pSeed.NormalizeHostname(new Uri(link.Url, UriKind.Absolute).Host))
                .Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToImmutableArray();
            entries.Add(new(result.Hostname, result.DestinationHostname, counts[result.Hostname], Summary(result),
                result.RobotsRequestOutcome, result.RobotsProxyClassification, result.RobotsObservation, result.RobotsResponse?.StatusCode,
                result.HomepageRequestAttempted, result.HomepageRequestOutcome, result.HomepageProxyClassification,
                result.HomepageResponse?.StatusCode, MediaType(result.HomepageResponse?.ContentType), Title(result.HomepageMetadata?.Title),
                links, result.HomepageMetadata == null || result.HomepageMetadata.ExtractionIncomplete || result.HomepageMetadata.BodyTruncated));
        }
        // Public ordering does not depend on private Base64 spelling. Never choose a preferred destination.
        return new(raw.StartedAtUtc, raw.CompletedAtUtc, entries.OrderBy(e => e.Hostname, StringComparer.Ordinal)
            .ThenBy(e => e.DestinationHostname, StringComparer.Ordinal).ToImmutableArray());
    }

    private static I2pPublicSummaryState Summary(I2pRawSeedResult result)
    {
        if (result.HomepageResponse != null) return I2pPublicSummaryState.HomepageResponseObserved;
        if (result.HomepageRequestAttempted == true) return I2pPublicSummaryState.HomepageAttemptFailed;
        if (result.RobotsObservation == I2pRobotsObservation.DisallowsRoot) return I2pPublicSummaryState.RobotsDisallowed;
        if (result.RobotsProxyClassification == I2pProxyResponseClassification.LikelyProxyFailure) return I2pPublicSummaryState.ProxyFailureObserved;
        if (result.RobotsRequestOutcome == I2pRequestOutcome.TimedOut) return I2pPublicSummaryState.RequestTimedOut;
        if (result.RobotsRequestOutcome is I2pRequestOutcome.TransportFailure or I2pRequestOutcome.ProxyUnavailable)
            return I2pPublicSummaryState.TransportFailure;
        if (result.RobotsObservation == I2pRobotsObservation.Redirect) return I2pPublicSummaryState.RobotsRedirect;
        return I2pPublicSummaryState.Ambiguous;
    }

    private static string? MediaType(string? text) => MediaTypeHeaderValue.TryParse(text, out var type) &&
        type.MediaType is { Length: > 0 and <= 80 } media && media.Contains('/') &&
        media.All(c => char.IsAsciiLetterOrDigit(c) || c is '/' or '-' or '.' or '+') ? media.ToLowerInvariant() : null;

    // Text only. A future HTML viewer must still escape this value for its output context.
    private static string? Title(string? text)
    {
        if (text == null) return null;
        var title = new StringBuilder();
        foreach (var rune in text.EnumerateRunes())
        {
            if (Rune.GetUnicodeCategory(rune) is UnicodeCategory.Control or UnicodeCategory.Format or
                UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator) continue;
            if (title.Length + rune.Utf16SequenceLength > MaxTitleCharacters) break;
            title.Append(rune.ToString());
        }
        var result = title.ToString().Trim();
        return result.Length == 0 ? null : result;
    }
}
